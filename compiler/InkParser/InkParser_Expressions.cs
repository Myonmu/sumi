using System;
using Ink.Parsed;
using System.Collections.Generic;

namespace Ink
{
	public partial class InkParser
	{
        protected Parsed.Object TempDeclarationOrAssignment()
        {
            Whitespace ();

            bool isNewDeclaration = ParseTempKeyword();

            Whitespace ();

            // Support dotted LHS for field assignment: ~ Oswald.name = x
            List<Identifier> pathIds = null;
            Identifier varIdentifier = null;
            if (isNewDeclaration) {
                varIdentifier = (Identifier)Expect (IdentifierWithMetadata, "variable name");
            } else {
                pathIds = Parse (DotAccessCall);
                if (pathIds == null || pathIds.Count == 0)
                    return null;
                varIdentifier = pathIds [0];
            }

            if (varIdentifier == null) {
                return null;
            }

            Whitespace();

            // Optional type for temp: ~ temp guest: Character [= expr]
            string structTypeName = null;
            if (isNewDeclaration && ParseString (":") != null) {
                Whitespace ();
                var typeId = Expect (IdentifierWithMetadata, "struct type name") as Identifier;
                structTypeName = typeId?.name;
                Whitespace ();
            }

            // += -=
            bool isIncrement = ParseString ("+") != null;
            bool isDecrement = ParseString ("-") != null;
            if (isIncrement && isDecrement) Error ("Unexpected sequence '+-'");

            bool hasAssign = ParseString ("=") != null;
            if (!hasAssign) {
                // temp x: Type without initializer is allowed
                if (isNewDeclaration && structTypeName != null) {
                    var typedTemp = new VariableAssignment (varIdentifier, (Expression)null);
                    typedTemp.isNewTemporaryDeclaration = true;
                    typedTemp.structTypeName = structTypeName;
                    return typedTemp;
                }
                if (isNewDeclaration) Error ("Expected '='");
                return null;
            }

            Expression assignedExpression = (Expression)Expect (Expression, "value expression to be assigned");

            if (isIncrement || isDecrement) {
                if (pathIds != null && pathIds.Count > 1)
                    return new IncDecExpression (pathIds, assignedExpression, isIncrement);
                var result = new IncDecExpression (varIdentifier, assignedExpression, isIncrement);
                return result;
            } else if (pathIds != null && pathIds.Count > 1) {
                // Field assignment
                return new StructFieldAssignment (pathIds, assignedExpression);
            } else {
                var result = new VariableAssignment (varIdentifier, assignedExpression);
                result.isNewTemporaryDeclaration = isNewDeclaration;
                result.structTypeName = structTypeName;
                return result;
            }
        }

        protected void DisallowIncrement (Parsed.Object expr)
        {
        	if (expr is Parsed.IncDecExpression)
        		Error ("Can't use increment/decrement here. It can only be used on a ~ line");
        }

        protected bool ParseTempKeyword()
        {
            var ruleId = BeginRule ();

            if (Parse (Identifier) == "temp") {
                SucceedRule (ruleId);
                return true;
            } else {
                FailRule (ruleId);
                return false;
            }
        }

        protected Parsed.Return ReturnStatement()
        {
            Whitespace ();

            var returnOrDone = Parse(Identifier);
            if (returnOrDone != "return") {
                return null;
            }

            Whitespace ();

            var expr = Parse(Expression);

            var returnObj = new Return (expr);
            return returnObj;
        }

		protected Expression Expression() {
			return Expression(minimumPrecedence:0);
		}

		// Pratt Parser
		// aka "Top down operator precedence parser"
		// http://journal.stuffwithstuff.com/2011/03/19/pratt-parsers-expression-parsing-made-easy/
		// Algorithm overview:
		// The two types of precedence are handled in two different ways:
		//   ((((a . b) . c) . d) . e)			#1
		//   (a . (b . (c . (d . e))))			#2
		// Where #1 is automatically handled by successive loops within the main 'while' in this function,
		// so long as continuing operators have lower (or equal) precedence (e.g. imagine some series of "*"s then "+" above.
		// ...and #2 is handled by recursion of the right hand term in the binary expression parser.
		// (see link for advice on how to extend for postfix and mixfix operators)
		protected Expression Expression(int minimumPrecedence)
		{
			Whitespace ();

			// First parse a unary expression e.g. "-a" or parethensised "(1 + 2)"
			var expr = ExpressionUnary ();
			if (expr == null) {
                return null;
			}

			Whitespace ();

			// Attempt to parse (possibly multiple) continuing infix expressions (e.g. 1 + 2 + 3)
			while(true) {
				var ruleId = BeginRule ();

				// Operator
				var infixOp = ParseInfixOperator ();
				if (infixOp != null && infixOp.precedence > minimumPrecedence) {

					// Expect right hand side of operator
					var expectationMessage = string.Format("right side of '{0}' expression", infixOp.type);
					var multiaryExpr = Expect (() => ExpressionInfixRight (left: expr, op: infixOp), expectationMessage);
                    if (multiaryExpr == null) {

                        // Fail for operator and right-hand side of multiary expression
                        FailRule (ruleId);

                        return null;
                    }

                    expr = SucceedRule(ruleId, multiaryExpr) as Parsed.Expression;

					continue;
				}

                FailRule (ruleId);
				break;
			}

            Whitespace ();

            return expr;
		}

        protected Expression ExpressionUnary()
		{
            // Divert target is a special case - it can't have any other operators
            // applied to it, and we also want to check for it first so that we don't
            // confuse "->" for subtraction.
            var divertTarget = Parse (ExpressionDivertTarget);
            if (divertTarget != null) {
                return divertTarget;
            }

            var prefixOp = (string) OneOf (String ("-"), String ("!"));

            // Don't parse like the string rules above, in case its actually
            // a variable that simply starts with "not", e.g. "notable".
            // This rule uses the Identifier rule, which will scan as much text
            // as possible before returning.
            if (prefixOp == null) {
                prefixOp = Parse(ExpressionNot);
            }

			Whitespace ();

            // - Since we allow numbers at the start of variable names, variable names are checked before literals
            // - Function calls before variable names in case we see parentheses
            var expr = OneOf (ExpressionList, ExpressionVoidLiteral, ExpressionParen, ExpressionFunctionCall, ExpressionVariableName, ExpressionLiteral) as Expression;

            // Only recurse immediately if we have one of the (usually optional) unary ops
            if (expr == null && prefixOp != null) {
                expr = ExpressionUnary ();
            }

			if (expr == null)
                return null;

            if (prefixOp != null) {
                expr = UnaryExpression.WithInner(expr, prefixOp);
			}

            Whitespace ();

            var postfixOp = (string) OneOf (String ("++"), String ("--"));
            if (postfixOp != null) {
                bool isInc = postfixOp == "++";

                if (!(expr is VariableReference)) {
                    Error ("can only increment and decrement variables, but saw '" + expr + "'");

                    // Drop down and succeed without the increment after reporting error
                } else {
                    var varRef = (VariableReference)expr;
                    if (varRef.pathIdentifiers != null && varRef.pathIdentifiers.Count > 1)
                        expr = new IncDecExpression (varRef.pathIdentifiers, null, isInc);
                    else
                        expr = new IncDecExpression(varRef.identifier, isInc);
                }

            }

            return expr;
		}

        protected string ExpressionNot()
        {
            var id = Identifier ();
            if (id == "not") {
                return id;
            }

            return null;
        }

        /// <summary>
        /// Empty <c>[]</c> — used to remove a dynamic slot: <c>~ bag.x = []</c>.
        /// (Empty <c>()</c> remains an empty list literal.)
        /// </summary>
        protected Expression ExpressionVoidLiteral()
        {
            var ruleId = BeginRule ();
            Whitespace ();
            if (ParseString ("[") == null) {
                FailRule (ruleId);
                return null;
            }
            Whitespace ();
            if (ParseString ("]") == null) {
                FailRule (ruleId);
                return null;
            }
            return (Expression) SucceedRule (ruleId, new VoidLiteral ());
        }

		protected Expression ExpressionLiteral()
		{
            return (Expression) OneOf (ExpressionFloat, ExpressionInt, ExpressionBool, ExpressionString);
		}

        protected Expression ExpressionDivertTarget()
        {
            Whitespace ();

            var divert = Parse(SingleDivert);
            if (divert == null)
                return null;

            if (divert.isThread)
                return null;

            Whitespace ();

            return new DivertTarget (divert);
        }

        protected Number ExpressionInt()
        {
            int? intOrNull = ParseInt ();
            if (intOrNull == null) {
                return null;
            } else {
                return new Number (intOrNull.Value);
            }
        }

        protected Number ExpressionFloat()
        {
            float? floatOrNull = ParseFloat ();
            if (floatOrNull == null) {
                return null;
            } else {
                return new Number (floatOrNull.Value);
            }
        }

        protected StringExpression ExpressionString()
        {
            var openQuote = ParseString ("\"");
            if (openQuote == null)
                return null;

            // Set custom parser state flag so that within the text parser,
            // it knows to treat the quote character (") as an end character
            parsingStringExpression = true;

            List<Parsed.Object> textAndLogic = Parse (MixedTextAndLogic);

            Expect (String ("\""), "close quote for string expression");

            parsingStringExpression = false;

            if (textAndLogic == null) {
                textAndLogic = new List<Ink.Parsed.Object> ();
                textAndLogic.Add (new Parsed.Text (""));
            }

            else if (textAndLogic.Exists (c => c is Divert))
                Error ("String expressions cannot contain diverts (->)");

            return new StringExpression (textAndLogic);
        }

        protected Number ExpressionBool()
        {
            var id = Parse(Identifier);
            if (id == "true") {
                return new Number (true);
            } else if (id == "false") {
                return new Number (false);
            }

            return null;
        }

        /// <summary>
        /// Successive dot access ( a.b.c ), including evaluated components ( a.{x} ).
        /// First component must be a literal identifier.
        /// </summary>
        protected List<Identifier> DotAccessCall()
        {
            return ParseDottedPath (allowSpacesAroundDots: false);
        }

        /// <summary>
        /// A path component after a dot: either a literal identifier or <c>{expression}</c>.
        /// </summary>
        protected Identifier PathComponentWithOptionalBraceExpr()
        {
            var id = Parse (IdentifierWithMetadata);
            if (id != null)
                return id;

            if (ParseString ("{") == null)
                return null;

            Whitespace ();
            var expr = (Expression)Expect (Expression, "expression inside '{...}' for evaluated path component");
            Whitespace ();
            Expect (String ("}"), "closing '}' for evaluated path component");

            if (expr == null)
                return null;

            return new Identifier {
                name = null,
                dynamicNameExpression = expr,
                debugMetadata = null
            };
        }

        /// <summary>
        /// Dotted path starting with a literal identifier; further components may be <c>{expr}</c>.
        /// </summary>
        protected List<Identifier> ParseDottedPath (bool allowSpacesAroundDots)
        {
            var first = Parse (IdentifierWithMetadata);
            if (first == null)
                return null;

            var path = new List<Identifier> { first };

            while (true) {
                int nextRuleId = BeginRule ();

                if (allowSpacesAroundDots)
                    Whitespace ();

                if (ParseString (".") == null) {
                    FailRule (nextRuleId);
                    break;
                }

                if (allowSpacesAroundDots)
                    Whitespace ();

                var comp = (Identifier)Expect (PathComponentWithOptionalBraceExpr, "name or '{expression}' after '.'");
                if (comp == null) {
                    FailRule (nextRuleId);
                    break;
                }

                SucceedRule (nextRuleId);
                path.Add (comp);
            }

            return path;
        }

        protected Expression ExpressionFunctionCall()
        {
            var iden = Parse(DotAccessCall);
            if (iden == null || iden.Count == 0)
                return null;

            Whitespace ();
            
            var arguments = Parse(ExpressionFunctionCallArguments);
            if (arguments == null) {
                return null;
            }

            return new FunctionCall(iden, arguments);
        }

        protected List<Expression> ExpressionFunctionCallArguments()
        {
            if (ParseString ("(") == null)
                return null;

            // "Exclude" requires the rule to succeed, but causes actual comma string to be excluded from the list of results
            ParseRule commas = Exclude (String (","));
            var arguments = Interleave<Expression>(Expression, commas);
            if (arguments == null) {
                arguments = new List<Expression> ();
            }

            Whitespace ();

            Expect (String (")"), "closing ')' for function call");

            return arguments;
        }

        protected Expression ExpressionVariableName()
        {
            var ruleId = BeginRule ();

            // Allow 'none' as a REFVAR empty literal
            var peekId = Parse (Identifier);
            if (peekId == "none") {
                return (Expression) SucceedRule (ruleId, new NoneLiteral ());
            }
            FailRule (ruleId);

            List<Identifier> path = ParseDottedPath (allowSpacesAroundDots: true);

            // Allow 'self' as a receiver (Python-style); allow 'dynamic'/'struct' for is/isnt kind queries.
            // Other reserved keywords stay invalid as names.
            if (path == null)
                return null;
            if (Story.IsReservedKeyword (path[0].name)
                && path[0].name != "self"
                && path[0].name != "dynamic"
                && path[0].name != "struct")
                return null;

            return new VariableReference (path);
        }

		protected Expression ExpressionParen()
		{
			if (ParseString ("(") == null)
                return null;

            var innerExpr = Parse(Expression);
			if (innerExpr == null)
                return null;

			Whitespace ();

            Expect (String(")"), "closing parenthesis ')' for expression");

            return innerExpr;
		}

		protected Expression ExpressionInfixRight(Parsed.Expression left, InfixOperator op)
		{
			Whitespace ();

            var right = Parse(() => Expression (op.precedence));
			if (right) {

				// We assume that the character we use for the operator's type is the same
				// as that used internally by e.g. Runtime.Expression.Add, Runtime.Expression.Multiply etc
				var expr = new BinaryExpression (left, right, op.type);
                return expr;
			}

            return null;

		}

        protected Parsed.List ExpressionList ()
        {
            Whitespace ();

            if (ParseString ("(") == null)
                return null;

            Whitespace ();

            // When list has:
            //  - 0 elements (null list) - this is okay, it's an empty list: "()"
            //  - 1 element - it could be confused for a single non-list related
            //    identifier expression in brackets, but this is a useless thing
            //    to do, so we reserve that syntax for a list with one item.
            //  - 2 or more elements - normal!
            List<Identifier> memberNames = SeparatedList (ListMember, Spaced (String (",")));

            Whitespace ();

            // May have failed to parse the inner list - the parentheses may
            // be for a normal expression
            if (ParseString (")") == null)
                return null;

            return new List (memberNames);
        }

        protected Identifier ListMember ()
        {
            Whitespace ();

            Identifier identifier = Parse (IdentifierWithMetadata);
            if (identifier == null)
                return null;

            var dot = ParseString (".");
            if (dot != null) {
                Identifier identifier2 = Expect (IdentifierWithMetadata, "element name within the set " + identifier) as Identifier;
                identifier.name = identifier.name + "." + identifier2?.name;
            }

            Whitespace ();

            return identifier;
        }

		void RegisterExpressionOperators()
		{
            // These will be tried in order, so we need "<=" before "<"
            // for correctness

            RegisterBinaryOperator ("&&", precedence:1);
            RegisterBinaryOperator ("||", precedence:1);
            RegisterBinaryOperator ("and", precedence:1, requireWhitespace: true);
            RegisterBinaryOperator ("or", precedence:1, requireWhitespace: true);

            RegisterBinaryOperator ("==", precedence:2);
            RegisterBinaryOperator (">=", precedence:2);
            RegisterBinaryOperator ("<=", precedence:2);
            RegisterBinaryOperator ("<", precedence:2);
            RegisterBinaryOperator (">", precedence:2);
            RegisterBinaryOperator ("!=", precedence:2);

            // (apples, oranges) + cabbages has (oranges, cabbages) == true
            RegisterBinaryOperator ("?", precedence: 3);
            RegisterBinaryOperator ("has", precedence: 3, requireWhitespace:true);
            RegisterBinaryOperator ("!?", precedence: 3);
            RegisterBinaryOperator ("hasnt", precedence: 3, requireWhitespace: true);
            RegisterBinaryOperator ("^", precedence: 3);

            // Struct polymorphism: instance is Type / instance isnt Type
            RegisterBinaryOperator ("is", precedence: 3, requireWhitespace: true);
            RegisterBinaryOperator ("isnt", precedence: 3, requireWhitespace: true);

			RegisterBinaryOperator ("+", precedence:4);
			RegisterBinaryOperator ("-", precedence:5);
			RegisterBinaryOperator ("*", precedence:6);
			RegisterBinaryOperator ("/", precedence:7);

            RegisterBinaryOperator ("%", precedence:8);
            RegisterBinaryOperator ("mod", precedence:8, requireWhitespace:true);
		}
	}
}

