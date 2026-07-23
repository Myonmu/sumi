using System.Collections.Generic;

namespace Ink.Parsed
{
    /// <summary>
    /// Shared codegen for dotted struct/dynamic/path access, including
    /// runtime-evaluated components: <c>something.{expr}</c>.
    /// </summary>
    static class StructPathCodegen
    {
        public static void AddDynamicNameContent (Object parent, IList<Identifier> path)
        {
            if (parent == null || path == null)
                return;
            foreach (var id in path) {
                if (id != null && id.dynamicNameExpression != null)
                    parent.AddContent (id.dynamicNameExpression);
            }
        }

        public static List<string> PathNames (IList<Identifier> path)
        {
            var names = new List<string> ();
            if (path == null)
                return names;
            foreach (var id in path)
                names.Add (id != null && id.isDynamic ? null : id?.name);
            return names;
        }

        public static bool HasDynamicComponent (IList<Identifier> path)
        {
            if (path == null)
                return false;
            foreach (var id in path) {
                if (id != null && id.isDynamic)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Push a field/slot name onto the evaluation stack (literal string or evaluated expression).
        /// </summary>
        public static void GenerateNameOntoStack (Runtime.Container container, Identifier id)
        {
            if (id == null)
                return;
            if (id.isDynamic)
                id.dynamicNameExpression.GenerateIntoContainer (container);
            else
                container.AddContent (new Runtime.StringValue (id.name));
        }

        public static void GenerateFieldGet (Runtime.Container container, Identifier id)
        {
            if (id == null)
                return;
            if (id.isDynamic) {
                id.dynamicNameExpression.GenerateIntoContainer (container);
                container.AddContent (new Runtime.StructFieldGet (null));
            } else {
                container.AddContent (new Runtime.StructFieldGet (id.name));
            }
        }

        /// <summary>
        /// Emit StructFieldSet. When <paramref name="id"/> is dynamic, pushes the name
        /// after the value is already on the stack (stack: instance, value, name).
        /// </summary>
        public static void GenerateFieldSet (Runtime.Container container, Identifier id)
        {
            if (id == null)
                return;
            if (id.isDynamic) {
                id.dynamicNameExpression.GenerateIntoContainer (container);
                container.AddContent (new Runtime.StructFieldSet (null));
            } else {
                container.AddContent (new Runtime.StructFieldSet (id.name));
            }
        }

        /// <summary>
        /// Build a dotted path string on the evaluation stack from mixed literal/dynamic components.
        /// </summary>
        public static void GeneratePathStringOntoStack (Runtime.Container container, IList<Identifier> components)
        {
            if (components == null || components.Count == 0)
                return;

            bool havePath = false;
            foreach (var comp in components) {
                if (havePath) {
                    container.AddContent (new Runtime.StringValue ("."));
                    container.AddContent (Runtime.NativeFunctionCall.CallWithName ("+"));
                }

                GenerateNameOntoStack (container, comp);

                if (havePath) {
                    container.AddContent (Runtime.NativeFunctionCall.CallWithName ("+"));
                } else if (comp != null && comp.isDynamic) {
                    // Ensure first dynamic component is a string for later concatenations / divert
                    container.AddContent (new Runtime.StringValue (""));
                    container.AddContent (Runtime.NativeFunctionCall.CallWithName ("+"));
                }

                havePath = true;
            }
        }
    }
}
