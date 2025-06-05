using Ink.Parsed;
namespace Ink
{
    public partial class InkPreprocessor
    {
        object ParsePreprocessorExpression()
        {
            Whitespace();
            var exp = Parse(Expression);
            _currentExpression = exp;
            return ParseSuccess;
        }

        /* The following code is mostly copied from InkParser_Expressions
         *  But since we have less grammar in preprocessor directives,
         *  these functions are simplified.
         */
        
        protected IPreprocessorEvaluable Expression(int minimumPrecedence)
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

                    expr = SucceedRule(ruleId, multiaryExpr) as IPreprocessorEvaluable;

                    continue;
                }

                FailRule (ruleId);
                break;
            }

            Whitespace ();

            return expr;
        }
        
        protected IPreprocessorEvaluable ExpressionInfixRight(IPreprocessorEvaluable left, InfixOperator op)
        {
            Whitespace ();

            var right = Parse(() => Expression (op.precedence));
            if (right != null) {

                // We assume that the character we use for the operator's type is the same
                // as that used internally by e.g. Runtime.Expression.Add, Runtime.Expression.Multiply etc
                var expr = new BinaryExpression (left as Expression, right as Expression, op.type);
                return expr;
            }

            return null;

        }

        protected IPreprocessorEvaluable Expression()
        {
            return Expression(minimumPrecedence:0);
        }
        
        protected IPreprocessorEvaluable ExpressionParen()
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

        protected IPreprocessorEvaluable ExpressionPreprocessorImplicitBoolean()
        {
            var identifier = Parse(Identifier);
            if (identifier == null) return null;
            return new PreprocessorExpression(identifier);
        }
        
        protected IPreprocessorEvaluable ExpressionUnary()
		{
            var prefixOp = (string) ParseObject(String ("!"));

            // in main ink, we may have negate operator written as "not". 
            // this is not allowed here, so we skip that "not"-parsing logic.

			Whitespace ();

            // - Since we allow numbers at the start of variable names, variable names are checked before literals
            // - Function calls before variable names in case we see parentheses
            var expr = OneOf(ExpressionParen, ExpressionPreprocessorImplicitBoolean) as IPreprocessorEvaluable;

            // Only recurse immediately if we have one of the (usually optional) unary ops
            if (expr == null && prefixOp != null) {
                expr = ExpressionUnary ();
            }

			if (expr == null)
                return null;

            if (prefixOp != null) {
                expr = UnaryExpression.WithInner(expr as Expression, prefixOp) as IPreprocessorEvaluable;
			}

            Whitespace ();

            // no post-fix operators. skip ++ and -- parses

            return expr;
		}

        void RegisterExpressionOperators()
        {
            RegisterBinaryOperator("&&", precedence: 1);
            RegisterBinaryOperator("||", precedence: 1);
        }

        /**
         *  Copied from InkParser_Logic.
         *  Here we do not allow extended character ranges.
         */
        protected string Identifier()
        {
            // Parse remaining characters (if any)
            var name = ParseCharactersFromCharSet (identifierCharSet);
            if (name == null)
                return null;
            
            // disallow identifier that starts with a number 
            // (in main ink this is allowed, but not in preprocessor directives)
            if (name.Length > 0 && (name[0] >= '0' && name[0] <= '9'))
            {
                return null;
            }
                
            return name;
        }

        CharacterSet identifierCharSet {
            get {
                if (_identifierCharSet == null) {
                    (_identifierCharSet = new CharacterSet ())
                        .AddRange ('A', 'Z')
                        .AddRange ('a', 'z')
                        .AddRange ('0', '9')
                        .Add ('_');
                }
                return _identifierCharSet;
            }
        }

        private CharacterSet _identifierCharSet;
    }
}