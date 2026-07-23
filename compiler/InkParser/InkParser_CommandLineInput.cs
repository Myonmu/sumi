using System.Collections.Generic;

namespace Ink
{
    public partial class InkParser
    {
        // Valid returned objects:
        //  - "help"
        //  - int: for choice number
        //  - Parsed.Divert
        //  - Variable declaration/assignment
        //  - Epression
        //  - Lookup debug source for character offset
        //  - Lookup debug source for runtime path
        //  - SetBreakpoints / continue / play
        public CommandLineInput CommandLineUserInput()
        {
            CommandLineInput result = new CommandLineInput ();

            Whitespace ();

            if (ParseString ("help") != null) {
                result.isHelp = true;
                return result;
            }

            if (ParseString ("exit") != null || ParseString ("quit") != null) {
                result.isExit = true;
                return result;
            }

            if (ParseString ("continue") != null) {
                result.isContinue = true;
                return result;
            }

            if (ParseString ("play") != null) {
                result.isPlay = true;
                return result;
            }

            return (CommandLineInput) OneOf (
                SetBreakpoints,
                DebugSource,
                DebugPathLookup,
                InspectVar,
                UserChoiceNumber, 
                UserImmediateModeStatement
            );
        }

        CommandLineInput SetBreakpoints ()
        {
            Whitespace ();

            if (ParseString ("SetBreakpoints") == null)
                return null;

            var inputStruct = new CommandLineInput ();
            inputStruct.hasSetBreakpoints = true;
            inputStruct.breakpoints = new List<BreakpointSpec> ();

            // Optional whitespace-separated list: file.ink:12 other/file.ink:3
            // Empty list clears all breakpoints.
            while (true) {
                Whitespace ();

                var fileName = ParseCharactersFromCharSet (_breakpointFileCharSet);
                if (fileName == null)
                    break;

                if (ParseString (":") == null) {
                    Error ("expected ':' after breakpoint filename, e.g. SetBreakpoints story.ink:12");
                    return null;
                }

                int? lineNumber = ParseInt ();
                if (lineNumber == null) {
                    Error ("expected line number after ':', e.g. SetBreakpoints story.ink:12");
                    return null;
                }

                inputStruct.breakpoints.Add (new BreakpointSpec {
                    fileName = fileName,
                    lineNumber = (int)lineNumber
                });
            }

            return inputStruct;
        }

        CommandLineInput InspectVar ()
        {
            Whitespace ();

            if (ParseString ("InspectVar") == null)
                return null;

            if (Whitespace () == null)
                return null;

            var varName = Expect (Identifier, "variable name") as string;
            if (varName == null)
                return null;

            var inputStruct = new CommandLineInput ();
            inputStruct.inspectVariableName = varName;
            return inputStruct;
        }

        CommandLineInput DebugSource ()
        {
            Whitespace ();

            if (ParseString ("DebugSource") == null)
                return null;

            Whitespace ();

            var expectMsg = "character offset in parentheses, e.g. DebugSource(5)";
            if (Expect (String ("("), expectMsg) == null)
                return null;

            Whitespace ();

            int? characterOffset = ParseInt ();
            if (characterOffset == null) {
                Error (expectMsg);
                return null;
            }

            Whitespace ();

            Expect (String (")"), "closing parenthesis");

            var inputStruct = new CommandLineInput ();
            inputStruct.debugSource = characterOffset;
            return inputStruct;
        }

        CommandLineInput DebugPathLookup ()
        {
            Whitespace ();

            if (ParseString ("DebugPath") == null)
                return null;

            if (Whitespace () == null)
                return null;

            var pathStr = Expect (RuntimePath, "path") as string;

            var inputStruct = new CommandLineInput ();
            inputStruct.debugPathLookup = pathStr;
            return inputStruct;
        }

        string RuntimePath ()
        {
            if (_runtimePathCharacterSet == null) {
                _runtimePathCharacterSet = new CharacterSet (identifierCharSet);
                _runtimePathCharacterSet.Add ('-'); // for c-0, g-0 etc
                _runtimePathCharacterSet.Add ('.');

            }
            
            return ParseCharactersFromCharSet (_runtimePathCharacterSet);
        }

        CommandLineInput UserChoiceNumber()
        {
            Whitespace ();

            int? number = ParseInt ();
            if (number == null) {
                return null;
            }

            Whitespace ();

            if (Parse(EndOfLine) == null) {
                return null;
            }

            var inputStruct = new CommandLineInput ();
            inputStruct.choiceInput = number;
            return inputStruct;
        }

        CommandLineInput UserImmediateModeStatement()
        {
            var statement = OneOf (SingleDivert, TempDeclarationOrAssignment, Expression);

            var inputStruct = new CommandLineInput ();
            inputStruct.userImmediateModeStatement = statement;
            return inputStruct;
        }

        CharacterSet _runtimePathCharacterSet;

        CharacterSet _breakpointFileCharSet {
            get {
                if (_breakpointFileCharSetCached == null) {
                    _breakpointFileCharSetCached = new CharacterSet (identifierCharSet);
                    _breakpointFileCharSetCached.Add ('.');
                    _breakpointFileCharSetCached.Add ('/');
                    _breakpointFileCharSetCached.Add ('\\');
                    _breakpointFileCharSetCached.Add ('-');
                }
                return _breakpointFileCharSetCached;
            }
        }
        CharacterSet _breakpointFileCharSetCached;
    }
}

