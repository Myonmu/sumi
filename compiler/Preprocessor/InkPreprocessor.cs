using System.Collections.Generic;
using System.Text;
namespace Ink
{
    /// <summary>
    /// Sumi added text preprocess directive processor.
    /// Works similarly to c#'s "#if...#elif...#else...#endif" statements.
    /// The implementation is not complete, as we do not support:
    ///  - directive inside directive ( #IF A ... #IF B ... #ENDIF #ENDIF )
    /// </summary>
    public partial class InkPreprocessor : BaseParser
    {
        class PreprocessorBranch
        {
            public int startLine; // includes #IF
            public int endLine; // does not include the closing directive
            public int contentStartIndex;
            public int contentEndIndex;
            public IPreprocessorEvaluable expression;

            public bool Match(HashSet<string> enabledSymbols)
            {
                return expression == null || enabledSymbols != null && expression.PreprocessorEvaluate(enabledSymbols);
            }
        }

        private HashSet<string> _enabledSymbols;
        private List<PreprocessorBranch> _preprocessorBranches = new List<PreprocessorBranch>();
        private int _preprocessorBlocksCount;

        private int _startLine, _endLine, _contentStartIndex, _contentEndIndex;
        private IPreprocessorEvaluable _currentExpression;
        private bool _expectEndIf, _isClosed;
        public InkPreprocessor(string source, HashSet<string> enabledSymbols) : base(source)
        {
            _enabledSymbols = enabledSymbols;
            RegisterExpressionOperators();
        }

        public string Process()
        {
            var stringList = Interleave<string>(Optional(PreprocessorsAndNewLines), Optional(MainInk));
            if (stringList != null)
            {
                return string.Join("", stringList.ToArray());
            }
            else
            {
                return null;
            }
        }

        string MainInk()
        {
            return ParseUntil(PreprocessorsAndNewLines, _preprocessorOrNewlineStartCharacter, null);
        }

        string PreprocessorsAndNewLines()
        {
            var newlines = Interleave<string>(Optional(ParseNewline), Optional(PreprocessorGroup));

            if (newlines != null)
            {
                return string.Join("", newlines.ToArray());
            }
            else
            {
                return null;
            }
        }

        void RecordStart()
        {
            _startLine = lineIndex;
            _contentStartIndex = index;
        }

        void AddBlock()
        {
            _endLine = lineIndex + 1;
            if (_preprocessorBlocksCount >= _preprocessorBranches.Count)
            {
                _preprocessorBranches.Add(new PreprocessorBranch());
            }
            var block = _preprocessorBranches[_preprocessorBlocksCount];
            block.startLine = _startLine;
            block.endLine = _endLine;
            block.contentStartIndex = _contentStartIndex;
            block.contentEndIndex = _contentEndIndex;
            block.expression = _currentExpression;
            _preprocessorBlocksCount++;
        }

        string CreateResult()
        {
            var sb = new StringBuilder();
            var branchSelected = false;
            for (int i = 0; i < _preprocessorBlocksCount; i++)
            {
                var block = _preprocessorBranches[i];
                if (!branchSelected && block.Match(_enabledSymbols))
                {
                    branchSelected = true;
                    sb.Append('\n');
                    for (int j = block.contentStartIndex; j < block.contentEndIndex; j++)
                    {
                        sb.Append(this.inputString[j]);
                    }
                }
                else
                {
                    // replace everything with empty lines
                    for (int j = block.startLine; j < block.endLine; j++)
                    {
                        sb.Append('\n');
                    }
                }
            }
            sb.Append('\n');
            return sb.ToString();
        }

        void DirectiveEndOfLine()
        {
            AnyWhitespace();
            EndOfLine();
            RecordStart();
        }

        object If()
        {
            if (ParseString(IF) == null) return null;
            _expectEndIf = false;
            _isClosed = false;
            _preprocessorBlocksCount = 0;
            if (ParseObject(ParsePreprocessorExpression) == null) return null;
            DirectiveEndOfLine();
            return ParseSuccess;
        }

        object Elif()
        {
            _contentEndIndex = index;
            if (ParseString(ELIF) == null) return null;
            AddBlock();
            if (ParseObject(ParsePreprocessorExpression) == null) return null;
            DirectiveEndOfLine();
            return ParseSuccess;
        }

        object Else()
        {
            _contentEndIndex = index;
            if (ParseString(ELSE) == null) return null;
            AddBlock();
            DirectiveEndOfLine();
            _expectEndIf = true;
            _currentExpression = null;
            return ParseSuccess;
        }

        object EndIf()
        {
            _contentEndIndex = index;
            if (ParseString(ENDIF) == null) return null;
            AddBlock();
            DirectiveEndOfLine();
            _isClosed = true;
            return ParseSuccess;
        }

        object AnyDirectiveLiteral()
        {
            if (!_expectEndIf)
            {
                return OneOf(String(ELIF), String(ELSE), String(ENDIF));
            }
            return String(ENDIF);
        }
        object AnyDirectiveDeclaration()
        {
            if (!_expectEndIf)
            {
                return OneOf(Elif, Else, EndIf);
            }
            return ParseObject(EndIf);
        }

        string PreprocessorGroup()
        {
            if (ParseObject(If) == null) return null;
            do
            {
                ParseUntil(AnyDirectiveLiteral, _preprocessorStartChar, null);
                if (ParseObject(AnyDirectiveDeclaration) == null) break;
            } while (!_isClosed);
            if (_isClosed)
            {
                var replacement = CreateResult();
                return replacement;
            }
            return null;
        }



        private const string IF = "#IF";
        private const string ELIF = "#ELIF";
        private const string ELSE = "#ELSE";
        private const string ENDIF = "#ENDIF";
        CharacterSet _whitespaceOrNewlineCharacters = new CharacterSet(" \t\r\n");
        CharacterSet _preprocessorStartChar = new CharacterSet("#");
        CharacterSet _preprocessorOrNewlineStartCharacter = new CharacterSet("#\r\n");
        CharacterSet _newlineCharacters = new CharacterSet("\n\r");
    }
}