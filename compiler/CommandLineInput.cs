using System.Collections.Generic;

namespace Ink
{
    public class CommandLineInput
    {
        public bool isHelp;
        public bool isExit;
        public bool isContinue;
        public bool isPlay;
        public bool hasSetBreakpoints;
        public List<BreakpointSpec> breakpoints;
        public int? choiceInput;
        public int? debugSource;
        public string debugPathLookup;
        public string inspectVariableName;
        public object userImmediateModeStatement;
    }

    public struct BreakpointSpec
    {
        public string fileName;
        public int lineNumber;
    }
}