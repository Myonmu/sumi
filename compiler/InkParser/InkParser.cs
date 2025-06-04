using System;
using System.Collections.Generic;
using System.Text;

namespace Ink
{
    public partial class InkParser : BaseParser
    {
        public InkParser(string str,
            string filenameForMetadata = null, 
            Ink.ErrorHandler externalErrorHandler = null, 
            IFileHandler fileHandler = null, 
            HashSet<string> preprocessorDirectives = null)
            : this(str, filenameForMetadata, externalErrorHandler, null, fileHandler, preprocessorDirectives)
        {  }

        InkParser(string str, 
            string inkFilename = null, 
            Ink.ErrorHandler externalErrorHandler = null, 
            InkParser rootParser = null, 
            IFileHandler fileHandler = null, 
            HashSet<string> preprocessorDirectives = null) : base(str, true) {
            
            _preprocessorDirectives = preprocessorDirectives;
            _filename = inkFilename;
            
            PreProcess(str);
            
            RegisterExpressionOperators ();
            GenerateStatementLevelRules ();

            // Built in handler for all standard parse errors and warnings
            this.errorHandler = OnStringParserError;

            // The above parse errors are then formatted as strings and passed
            // to the Ink.ErrorHandler, or it throws an exception
            _externalErrorHandler = externalErrorHandler;

            _fileHandler = fileHandler ?? new DefaultFileHandler();

            if (rootParser == null) {
                _rootParser = this;

                _openFilenames = new HashSet<string> ();

                if (inkFilename != null) {
                    var fullRootInkPath = _fileHandler.ResolveInkFilename (inkFilename);
                    _openFilenames.Add (fullRootInkPath);
                }

            } else {
                _rootParser = rootParser;
            }

        }

        // Main entry point
        public Parsed.Story Parse()
        {
            List<Parsed.Object> topLevelContent = StatementsAtLevel (StatementLevel.Top);

            // Note we used to return null if there were any errors, but this would mean
            // that include files would return completely empty rather than attempting to
            // continue with errors. Returning an empty include files meant that anything
            // that *did* compile successfully would otherwise be ignored, generating way
            // more errors than necessary.
            return new Parsed.Story (topLevelContent, isInclude:_rootParser != this);
        }

        protected List<T> SeparatedList<T> (SpecificParseRule<T> mainRule, ParseRule separatorRule) where T : class
        {
            T firstElement = Parse (mainRule);
            if (firstElement == null) return null;

            var allElements = new List<T> ();
            allElements.Add (firstElement);

            do {

                int nextElementRuleId = BeginRule ();

                var sep = separatorRule ();
                if (sep == null) {
                    FailRule (nextElementRuleId);
                    break;
                }

                var nextElement = Parse (mainRule);
                if (nextElement == null) {
                    FailRule (nextElementRuleId);
                    break;
                }

                SucceedRule (nextElementRuleId);

                allElements.Add (nextElement);

            } while (true);

            return allElements;
        }

        private void PrintWithLineNumber(string s)
        {
            var lineNum = 1;
            var lastIndex = 0;
            var lineBreakIndex = 0;
            
            var sb = new StringBuilder($"{lineNum}> ");
            while (true)
            {
                lineBreakIndex = s.IndexOf('\n', lastIndex);
                if (lineBreakIndex < 0) break;
                sb.Append(s.Substring(lastIndex, lineBreakIndex - lastIndex + 1));
                lineNum++;
                sb.Append($"{lineNum}> ");
                lastIndex = lineBreakIndex + 1;
            }
            Console.WriteLine("\n\n\n\n================================================");
            Console.WriteLine(sb.ToString());
            Console.WriteLine("================================================\n\n\n\n");
        }

        protected override string PreProcessInputString(string str)
        {
            PrintWithLineNumber(str);
            var inputWithCommentsRemoved = (new CommentEliminator (str)).Process();
            var inputWithPreprocessorResolved = (new InkPreprocessor(inputWithCommentsRemoved, _preprocessorDirectives)).Process();
            PrintWithLineNumber(inputWithPreprocessorResolved);
            return inputWithPreprocessorResolved;
        }

        protected Runtime.DebugMetadata CreateDebugMetadata(StringParserState.Element stateAtStart, StringParserState.Element stateAtEnd)
        {
            var md = new Runtime.DebugMetadata ();
            md.startLineNumber = stateAtStart.lineIndex + 1;
            md.endLineNumber = stateAtEnd.lineIndex + 1;
            md.startCharacterNumber = stateAtStart.characterInLineIndex + 1;
            md.endCharacterNumber = stateAtEnd.characterInLineIndex + 1;
            md.fileName = _filename;
            return md;
        }

        protected override void RuleDidSucceed(object result, StringParserState.Element stateAtStart, StringParserState.Element stateAtEnd)
        {
            // Apply DebugMetadata based on the state at the start of the rule
            // (i.e. use line number as it was at the start of the rule)
            var parsedObj = result as Parsed.Object;
            if ( parsedObj) {
                parsedObj.debugMetadata = CreateDebugMetadata(stateAtStart, stateAtEnd);
                return;
            }

            // A list of objects that doesn't already have metadata?
            var parsedListObjs = result as List<Parsed.Object>;
            if (parsedListObjs != null) {
                foreach (var parsedListObj in parsedListObjs) {
                    if (!parsedListObj.hasOwnDebugMetadata) {
                        parsedListObj.debugMetadata = CreateDebugMetadata(stateAtStart, stateAtEnd);
                    }
                }
            }

            var id = result as Parsed.Identifier;
            if (id != null) {
                id.debugMetadata = CreateDebugMetadata(stateAtStart, stateAtEnd);
            }
        }

        protected bool parsingStringExpression
        {
            get {
                return GetFlag ((uint)CustomFlags.ParsingString);
            }
            set {
                SetFlag ((uint)CustomFlags.ParsingString, value);
            }
        }

        protected bool tagActive
        {
            get {
                return GetFlag ((uint)CustomFlags.TagActive);
            }
            set {
                SetFlag ((uint)CustomFlags.TagActive, value);
            }
        }

        protected enum CustomFlags {
            ParsingString = 0x1,
            TagActive = 0x2
        }

        void OnStringParserError(string message, int index, int lineIndex, bool isWarning)
        {
            var warningType = isWarning ? "WARNING:" : "ERROR:";
            string fullMessage;

            if (_filename != null) {
                fullMessage = string.Format(warningType+" '{0}' line {1}: {2}",  _filename, (lineIndex+1), message);
            } else {
                fullMessage = string.Format(warningType+" line {0}: {1}", (lineIndex+1), message);
            }

            if (_externalErrorHandler != null) {
                _externalErrorHandler (fullMessage, isWarning ? ErrorType.Warning : ErrorType.Error);
            } else {
                throw new System.Exception (fullMessage);
            }
        }

        IFileHandler _fileHandler;

        Ink.ErrorHandler _externalErrorHandler;

        string _filename;
        
        HashSet<string> _preprocessorDirectives;
    }
}

