using System.Collections.Generic;
namespace Ink
{
    public interface IPreprocessorEvaluable
    {
        bool PreprocessorEvaluate(HashSet<string> enabledSymbols);
    }
}