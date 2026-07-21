using System.Collections.Generic;
using Ink.Parsed;
using System.Linq;

namespace Ink
{
	public partial class InkParser
	{
        protected class NameWithMetadata {
            public string name;
            public Runtime.DebugMetadata metadata;
        }

        protected class KnotLikeDecl
        {
            public Identifier name;
        }
        
        protected class FlowDecl: KnotLikeDecl
        {
            public List<FlowBase.Argument> arguments;
            public bool isFunction;
        }

        protected class StructDecl: KnotLikeDecl
        {
            public List<Identifier> baseTypes;
        }

		protected Knot KnotDefinition()
		{
            var knotDecl = Parse(KnotDeclaration);
            if (!(knotDecl is FlowDecl flowDecl))
                return null;

			Expect(EndOfLine, "end of line after knot name definition", recoveryRule: SkipToNextLine);

			ParseRule innerKnotStatements = () => StatementsAtLevel (StatementLevel.Knot);

            var content = Expect (innerKnotStatements, "at least one line within the knot", recoveryRule: KnotStitchNoContentRecoveryRule) as List<Parsed.Object>;

            return new Knot (knotDecl.name, content, flowDecl.arguments, flowDecl.isFunction);
		}

        protected StructDeclaration StructDefinition()
        {
            var knotDecl = Parse(KnotDeclaration);
            if (!(knotDecl is StructDecl structDecl))
                return null;

            Expect(EndOfLine, "end of line after knot name definition", recoveryRule: SkipToNextLine);

            var content = ParseStructBodyContent ();

            return new StructDeclaration (knotDecl.name, content, structDecl.baseTypes);
        }

        /// <summary>
        /// Struct bodies accept fields, function stitches, and EXTERNAL lines.
        /// A blank line followed by VAR/REFVAR ends the struct so subsequent
        /// typed globals (e.g. VAR Oswald: Oswald) are parsed at top level.
        /// </summary>
        protected List<Parsed.Object> ParseStructBodyContent ()
        {
            var content = new List<Parsed.Object> ();
            bool blankLinePending = false;

            while (true) {
                var newlineCount = 0;
                while (ParseNewline () != null)
                    newlineCount++;
                // Previous Line() already consumed its trailing newline, so a single
                // extra newline here means a blank line before the next statement.
                if (newlineCount > 0)
                    blankLinePending = true;

                Whitespace ();

                // Next knot/struct ends the body
                var ruleId = BeginRule ();
                if (Parse (KnotDeclaration) != null) {
                    FailRule (ruleId); // don't consume — leave for outer parser
                    break;
                }
                FailRule (ruleId);

                // Blank line then VAR → hoist to top-level (global instances)
                if (blankLinePending) {
                    ruleId = BeginRule ();
                    var peekedVar = ParseObject (VariableDeclaration);
                    if (peekedVar != null) {
                        FailRule (ruleId);
                        break;
                    }
                    FailRule (ruleId);
                }

                var stitchDecl = Parse (StitchDeclaration);
                if (stitchDecl != null) {
                    Expect (EndOfLine, "end of line after stitch name", recoveryRule: SkipToNextLine);
                    var stitchContent = ParseStructMethodBodyContent ();
                    var stitch = new Stitch (stitchDecl.name, stitchContent, stitchDecl.arguments, stitchDecl.isFunction);
                    content.Add (stitch);
                    // After a method, further VARs are treated as top-level globals
                    // (fields must be declared before methods).
                    blankLinePending = true;
                    continue;
                }

                var ext = ParseObject (Line (ExternalDeclaration)) as Parsed.Object;
                if (ext != null) {
                    content.Add (ext);
                    blankLinePending = false;
                    continue;
                }

                var varDecl = ParseObject (Line (VariableDeclaration)) as Parsed.Object;
                if (varDecl != null) {
                    content.Add (varDecl);
                    blankLinePending = false;
                    continue;
                }

                // Skip pure whitespace / comments lines; otherwise stop
                if (ParseObject (Line (AuthorWarning)) != null)
                    continue;

                break;
            }

            return content;
        }

        /// <summary>
        /// Method bodies: text and logic lines only, ending at blank line / next stitch / knot / VAR.
        /// </summary>
        protected List<Parsed.Object> ParseStructMethodBodyContent ()
        {
            var content = new List<Parsed.Object> ();

            while (true) {
                // Skip spaces; handle newlines explicitly
                ParseCharactersFromString (" \t");

                var nlRule = BeginRule ();
                int nls = 0;
                while (ParseNewline () != null) nls++;
                if (nls >= 2) {
                    SucceedRule (nlRule);
                    break; // blank line
                }
                if (nls == 1) {
                    // Peek whether this newline starts a new struct-level construct
                    ParseCharactersFromString (" \t");
                    var peek = BeginRule ();
                    bool stop = Parse (KnotDeclaration) != null
                        || Parse (StitchDeclaration) != null
                        || ParseObject (VariableDeclaration) != null;
                    FailRule (peek);
                    if (stop) {
                        SucceedRule (nlRule);
                        break;
                    }
                    SucceedRule (nlRule); // consume newline, continue to next statement
                } else {
                    FailRule (nlRule);
                }

                ParseCharactersFromString (" \t");

                var endPeek = BeginRule ();
                bool atEnd = Parse (KnotDeclaration) != null
                    || Parse (StitchDeclaration) != null;
                FailRule (endPeek);
                if (atEnd)
                    break;

                // Only text / logic / return — not VAR or nested stitches
                var logic = ParseObject (LogicLine) as Parsed.Object;
                if (logic != null) {
                    content.Add (logic);
                    continue;
                }

                var textLine = ParseObject (LineOfMixedTextAndLogic);
                if (textLine is List<Parsed.Object> textList) {
                    content.AddRange (textList);
                    continue;
                }
                if (textLine is Parsed.Object textObj) {
                    content.Add (textObj);
                    continue;
                }

                if (ParseObject (Line (AuthorWarning)) != null)
                    continue;

                break;
            }

            if (content.Count == 0)
                content.Add (new Parsed.Text (""));

            return content;
        }

        protected KnotLikeDecl KnotDeclaration()
        {
            Whitespace ();

            if (KnotTitleEquals () == null)
                return null;

            Whitespace ();


            Identifier identifier = Parse(IdentifierWithMetadata);
            Identifier knotName;

            const string functionKeyword = "function";
            const string structKeyword = "struct";
            bool isFunc = identifier?.name == functionKeyword;
            bool isStruct = identifier?.name == structKeyword;
            var hint = isFunc ? functionKeyword : isStruct ? structKeyword : "knot";
            if (isFunc || isStruct) {
                Expect (Whitespace, $"whitespace after the '{hint}' keyword");
                knotName = Parse(IdentifierWithMetadata);
            }else {
                knotName = identifier;
            }

            if (knotName == null) {
                Error ($"Expected the name of the {hint}");
                knotName = new Identifier { name = "" }; // prevent later null ref
            }

            Whitespace ();
            List<FlowBase.Argument> parameterNames = null; 
            List<Parsed.Identifier> baseTypeIdentifiers = null;
            if (isStruct)
            {
                baseTypeIdentifiers = Parse(StructBaseType);
            }
            else
            {
                parameterNames = Parse (BracketedKnotDeclArguments);
            }

            Whitespace ();

            // Optional equals after name
            Parse(KnotTitleEquals);

            if (isStruct)
            {
                return new StructDecl()
                {
                    name = knotName,
                    baseTypes = baseTypeIdentifiers
                };
            }
            return new FlowDecl () { name = knotName, arguments = parameterNames, isFunction = isFunc };
        }

        protected List<Identifier> StructBaseType()
        {
            // ":" in struct decl denotes base type declaration
            var character = ParseSingleCharacter();
            var baseTypeIdentifiers = new HashSet<Identifier>();
            if (character == ':')
            {
                // parse multi-inheritance ( A: B, C, D )
                var shouldStop = false;
                while (!shouldStop)
                {
                    Whitespace();
                    var id = Parse(IdentifierWithMetadata);
                    shouldStop = id == null;
                    if (id != null)
                    {
                        shouldStop |= !baseTypeIdentifiers.Add(id);
                    }
                    shouldStop |= ParseSingleCharacter() != ',';
                }
                Whitespace();
            }
            return baseTypeIdentifiers.ToList();
        }

        protected string KnotTitleEquals()
        {
            // 2+ "=" starts a knot
            var multiEquals = ParseCharactersFromString ("=");
            if (multiEquals == null || multiEquals.Length <= 1) {
                return null;
            } else {
                return multiEquals;
            }
        }

		protected object StitchDefinition()
		{
            var decl = Parse(StitchDeclaration);
            if (decl == null)
                return null;

			Expect(EndOfLine, "end of line after stitch name", recoveryRule: SkipToNextLine);

			ParseRule innerStitchStatements = () => StatementsAtLevel (StatementLevel.Stitch);

            var content = Expect(innerStitchStatements, "at least one line within the stitch", recoveryRule: KnotStitchNoContentRecoveryRule) as List<Parsed.Object>;

            return new Stitch (decl.name, content, decl.arguments, decl.isFunction );
		}

        protected FlowDecl StitchDeclaration()
        {
            Whitespace ();

            // Single "=" to define a stitch
            if (ParseString ("=") == null)
                return null;

            // If there's more than one "=", that's actually a knot definition (or divert), so this rule should fail
            if (ParseString ("=") != null)
                return null;

            Whitespace ();

            // Stitches aren't allowed to be functions, but we parse it anyway and report the error later
            // ... that is no longer. Stitches can now be functions
            bool isFunc = ParseString ("function") != null;
            if ( isFunc ) {
                Whitespace ();
            }

            Identifier stitchName = Parse(IdentifierWithMetadata);
            if (stitchName == null)
                return null;

            Whitespace ();

            List<FlowBase.Argument> flowArgs = Parse(BracketedKnotDeclArguments);

            Whitespace ();

            // Optional trailing '=' (same style as knot titles): = function Foo() =
            ParseString ("=");

            Whitespace ();

            return new FlowDecl () { name = stitchName, arguments = flowArgs, isFunction = isFunc };
        }


		protected object KnotStitchNoContentRecoveryRule()
		{
            // Jump ahead to the next knot or the end of the file
            ParseUntil (KnotDeclaration, new CharacterSet ("="), null);

            var recoveredFlowContent = new List<Parsed.Object>();
			recoveredFlowContent.Add( new Parsed.Text("<ERROR IN FLOW>" ) );
			return recoveredFlowContent;
		}

        protected List<FlowBase.Argument> BracketedKnotDeclArguments()
        {
            if (ParseString ("(") == null)
                return null;

            var flowArguments = Interleave<FlowBase.Argument>(Spaced(FlowDeclArgument), Exclude (String(",")));

            Expect (String (")"), "closing ')' for parameter list");

            // If no parameters, create an empty list so that this method is type safe and
            // doesn't attempt to return the ParseSuccess object
            if (flowArguments == null) {
                flowArguments = new List<FlowBase.Argument> ();
            }

            return flowArguments;
        }

        protected FlowBase.Argument FlowDeclArgument()
        {
            // Possible forms:
            //  name
            //  -> name      (variable divert target argument
            //  ref name
            //  ref -> name  (variable divert target by reference)
            var firstIden = Parse(IdentifierWithMetadata);
            Whitespace ();
            var divertArrow = ParseDivertArrow ();
            Whitespace ();
            var secondIden = Parse(IdentifierWithMetadata);

            if (firstIden == null && secondIden == null)
                return null;


            var flowArg = new FlowBase.Argument ();
            if (divertArrow != null) {
                flowArg.isDivertTarget = true;
            }

            // Passing by reference
            if (firstIden != null && firstIden.name == "ref") {

                if (secondIden == null) {
                    Error ("Expected an parameter name after 'ref'");
                }

                flowArg.identifier = secondIden;
                flowArg.isByReference = true;
            }

            // Simple argument name
            else {

                if (flowArg.isDivertTarget) {
                    flowArg.identifier = secondIden;
                } else {
                    flowArg.identifier = firstIden;
                }

                if (flowArg.identifier == null) {
                    Error ("Expected an parameter name");
                }

                flowArg.isByReference = false;
            }

            return flowArg;
        }

        protected ExternalDeclaration ExternalDeclaration()
        {
            Whitespace ();

            Identifier external = Parse(IdentifierWithMetadata);
            if (external == null || external.name != "EXTERNAL")
                return null;

            Whitespace ();

            var funcIdentifier = Expect(IdentifierWithMetadata, "name of external function") as Identifier ?? new Identifier();

            Whitespace ();

            var parameterNames = Expect (BracketedKnotDeclArguments, "declaration of arguments for EXTERNAL, even if empty, i.e. 'EXTERNAL "+funcIdentifier+"()'") as List<FlowBase.Argument>;
            if (parameterNames == null)
                parameterNames = new List<FlowBase.Argument> ();

            var argNames = parameterNames.Select (arg => arg.identifier?.name).ToList();

            return new ExternalDeclaration (funcIdentifier, argNames);
        }

	}
}

