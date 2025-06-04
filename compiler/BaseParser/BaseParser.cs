namespace Ink
{

/* What happened to the refactoring?
 *
 *   1. InkParser_Whitespace is now part of the base parser (renamed accordingly)
 *
 */

    /// <summary>
    /// SUMI injected base parser
    /// </summary>
    public partial class BaseParser : StringParser
    {
        public BaseParser(string str, bool manuallyCallPreprocess = false) :
            base(str, manuallyCallPreprocess)
        {
        }
    }
}