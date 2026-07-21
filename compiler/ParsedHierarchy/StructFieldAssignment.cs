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
            bool targetIsRefVar = TargetFieldIsRefVar ();

            if (pathIdentifiers.Count == 2) {
                // Oswald.name = expr
                container.AddContent (new Runtime.VariablePointerValue (rootName));
                GenerateRhs (container, targetIsRefVar);
                container.AddContent (new Runtime.StructFieldSet (pathIdentifiers [1].name));
            } else {
                container.AddContent (new Runtime.VariableReference (rootName));
                for (int i = 1; i < pathIdentifiers.Count - 1; i++) {
                    container.AddContent (new Runtime.StructFieldGet (pathIdentifiers [i].name));
                }
                GenerateRhs (container, targetIsRefVar);
                container.AddContent (new Runtime.StructFieldSet (pathIdentifiers [pathIdentifiers.Count - 1].name));
            }

            container.AddContent (Runtime.ControlCommand.EvalEnd ());
            return container;
        }

        void GenerateRhs (Runtime.Container container, bool targetIsRefVar)
        {
            // REFVAR fields store a global name / none — not an evaluated struct value
            if (targetIsRefVar) {
                var rhsVar = expression as VariableReference;
                if (rhsVar != null && rhsVar.path != null && rhsVar.path.Count == 1 && rhsVar.name != "none") {
                    container.AddContent (new Runtime.StructRefValue (rhsVar.name));
                    return;
                }
                if (expression is NoneLiteral || (rhsVar != null && rhsVar.name == "none")) {
                    container.AddContent (new Runtime.StructRefValue (null));
                    return;
                }
            }

            if (expression != null)
                expression.GenerateIntoContainer (container);
            else
                container.AddContent (new Runtime.StructRefValue (null));
        }

        bool TargetFieldIsRefVar ()
        {
            if (story == null || pathIdentifiers == null || pathIdentifiers.Count < 2)
                return false;

            var path = new List<string> ();
            foreach (var id in pathIdentifiers)
                path.Add (id?.name);

            StructDeclaration type;
            int start;
            if (!story.TryResolveStructPathContext (path, this, out type, out start, reportErrors: false))
                return false;

            // Walk to the struct that owns the final field
            for (int i = start; i < path.Count - 1; i++) {
                var field = type.FindField (path [i]);
                if (field == null || string.IsNullOrEmpty (field.structTypeName))
                    return false;
                type = story.ResolveStruct (field.structTypeName);
                if (type == null)
                    return false;
            }

            var targetField = type.FindField (path [path.Count - 1]);
            return targetField != null && targetField.isRefVar;
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
