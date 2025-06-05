using System.Collections.Generic;
using Ink.Parsed;
using Ink.Runtime;
namespace Ink
{
    public class PreprocessorExpression: Expression, IPreprocessorEvaluable
    {
        public string identifier;

        public PreprocessorExpression(string identifier)
        {
            this.identifier = identifier;
        }

        public override void GenerateIntoContainer(Container container)
        {
            return;
        }
        public bool PreprocessorEvaluate(HashSet<string> enabledSymbols)
        {
            return enabledSymbols.Contains(identifier);
        }
    }
}