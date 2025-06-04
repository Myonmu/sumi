using System.Collections.Generic;
using System.Text;
namespace Ink
{
    /// <summary>
    /// Sumi added text preprocess directive processor.
    /// Works similarly to c#'s "#if...#elif...#else...#endif" statements.
    /// The implementation is not complete, as we do not support:
    ///  - logical operators ( #IF A && B || C || !D)
    ///  - directive inside directive ( #IF A ... #IF B ... #ENDIF #ENDIF )
    /// </summary>
    public class InkPreprocessor : BaseParser
    {
        class PreprocessorBlock
        {
            public int startLine; // includes #IF
            public int endLine; // does not include the closing directive
            public int actualContentStartIndex;
            public int actualContentEndIndex;
            public string directives;

            public bool Match(HashSet<string> enabledDirectives)
            {
                return string.IsNullOrEmpty(directives) ||
                       enabledDirectives != null &&
                       enabledDirectives.Contains(directives);
            }
        }

        private HashSet<string> _preprocessorDirectives;
        private List<PreprocessorBlock> _preprocessorBlocks = new List<PreprocessorBlock>();
        private int _preprocessorBlockEnd;

        private int _startLine, _endLine, _contentStart, _contentEnd;
        private string _currentDirectives;
        private bool _expectEndIf, _isClosed;
        public InkPreprocessor(string source, HashSet<string> preprocessorDirectives) : base(source)
        {
            _preprocessorDirectives = preprocessorDirectives;
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
            _contentStart = index;
        }

        void AddBlock()
        {
            _endLine = lineIndex + 1;
            if (_preprocessorBlockEnd >= _preprocessorBlocks.Count)
            {
                _preprocessorBlocks.Add(new PreprocessorBlock());
            }
            var block = _preprocessorBlocks[_preprocessorBlockEnd];
            block.startLine = _startLine;
            block.endLine = _endLine;
            block.actualContentStartIndex = _contentStart;
            block.actualContentEndIndex = _contentEnd;
            block.directives = _currentDirectives;
            _preprocessorBlockEnd++;
        }

        string CreateResult()
        {
            var sb = new StringBuilder();
            var branchSelected = false;
            for (int i = 0; i < _preprocessorBlockEnd; i++)
            {
                var block = _preprocessorBlocks[i];
                if (!branchSelected && block.Match(_preprocessorDirectives))
                {
                    branchSelected = true;
                    sb.Append('\n');
                    for (int j = block.actualContentStartIndex; j < block.actualContentEndIndex; j++)
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

        object ParseDirectives()
        {
            Whitespace();
            _currentDirectives = ParseUntilCharactersFromCharSet(_whitespaceOrNewlineCharacters);
            if (_currentDirectives == null) return null;
            return ParseSuccess;
        }

        object If()
        {
            if (ParseString(IF) == null) return null;
            _expectEndIf = false;
            _isClosed = false;
            _preprocessorBlockEnd = 0;
            if (ParseObject(ParseDirectives) == null) return null;
            DirectiveEndOfLine();
            return ParseSuccess;
        }

        object Elif()
        {
            _contentEnd = index;
            if (ParseString(ELIF) == null) return null;
            AddBlock();
            if (ParseObject(ParseDirectives) == null) return null;
            DirectiveEndOfLine();
            return ParseSuccess;
        }

        object Else()
        {
            _contentEnd = index;
            if (ParseString(ELSE) == null) return null;
            AddBlock();
            DirectiveEndOfLine();
            _expectEndIf = true;
            _currentDirectives = null;
            return ParseSuccess;
        }

        object EndIf()
        {
            _contentEnd = index;
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