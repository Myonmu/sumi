using System.Collections.Generic;

namespace Ink.Parsed
{
    public class NoneLiteral : Expression
    {
        public override void GenerateIntoContainer (Runtime.Container container)
        {
            container.AddContent (new Runtime.StructRefValue (null));
        }

        public override string ToString ()
        {
            return "none";
        }
    }

    /// <summary>
    /// Assignment to a struct field path: ~ Oswald.name = "x" or ~ party.scout = Oswald
    /// </summary>
    public class StructFieldAssignment : Object
    {
        public List<Identifier> pathIdentifiers;
        public Expression expression;

        public StructFieldAssignment (List<Identifier> path, Expression expr)
        {
            pathIdentifiers = path;
            expression = AddContent (expr);
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            var container = new Runtime.Container ();
            container.AddContent (Runtime.ControlCommand.EvalStart ());

            var rootName = pathIdentifiers [0].name;

            if (pathIdentifiers.Count == 2) {
                // Oswald.name = expr
                container.AddContent (new Runtime.VariablePointerValue (rootName));
                var rhsVar = expression as VariableReference;
                if (rhsVar != null && rhsVar.path != null && rhsVar.path.Count == 1 && rhsVar.name != "none") {
                    container.AddContent (new Runtime.StructRefValue (rhsVar.name));
                } else if (expression is NoneLiteral) {
                    container.AddContent (new Runtime.StructRefValue (null));
                } else {
                    expression.GenerateIntoContainer (container);
                }
                container.AddContent (new Runtime.StructFieldSet (pathIdentifiers [1].name));
            } else {
                container.AddContent (new Runtime.VariableReference (rootName));
                for (int i = 1; i < pathIdentifiers.Count - 1; i++) {
                    container.AddContent (new Runtime.StructFieldGet (pathIdentifiers [i].name));
                }
                expression.GenerateIntoContainer (container);
                container.AddContent (new Runtime.StructFieldSet (pathIdentifiers [pathIdentifiers.Count - 1].name));
            }

            container.AddContent (Runtime.ControlCommand.EvalEnd ());
            return container;
        }

        public override void ResolveReferences (Story context)
        {
            base.ResolveReferences (context);

            var path = new List<string> ();
            foreach (var id in pathIdentifiers)
                path.Add (id?.name);

            context.ValidateStructFieldAccess (path, this);
        }

        public override string typeName {
            get { return "struct field assignment"; }
        }
    }
}
