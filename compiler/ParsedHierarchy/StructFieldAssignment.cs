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

    /// <summary>Empty <c>[]</c> expression — used to remove a dynamic slot: <c>~ bag.x = []</c>.</summary>
    public class VoidLiteral : Expression
    {
        public override void GenerateIntoContainer (Runtime.Container container)
        {
            container.AddContent (new Runtime.Void ());
        }

        public override string ToString ()
        {
            return "[]";
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
            StructPathCodegen.AddDynamicNameContent (this, path);
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            var container = new Runtime.Container ();
            container.AddContent (Runtime.ControlCommand.EvalStart ());

            var rootName = pathIdentifiers [0].name;
            bool targetIsRefVar = TargetFieldIsRefVar ();
            var lastId = pathIdentifiers [pathIdentifiers.Count - 1];

            if (pathIdentifiers.Count == 2) {
                // Oswald.name = expr  /  Oswald.{n} = expr
                container.AddContent (new Runtime.VariablePointerValue (rootName));
                GenerateRhs (container, targetIsRefVar);
                StructPathCodegen.GenerateFieldSet (container, lastId);
            } else {
                container.AddContent (new Runtime.VariableReference (rootName));
                for (int i = 1; i < pathIdentifiers.Count - 1; i++) {
                    if (pathIdentifiers [i] != null && pathIdentifiers [i].name == "static" && i == 1
                        && !pathIdentifiers [i].isDynamic)
                        continue;
                    StructPathCodegen.GenerateFieldGet (container, pathIdentifiers [i]);
                }
                GenerateRhs (container, targetIsRefVar);
                StructPathCodegen.GenerateFieldSet (container, lastId);
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

            var path = StructPathCodegen.PathNames (pathIdentifiers);
            if (path [path.Count - 1] == null)
                return false;

            StructDeclaration type;
            int start;
            if (!story.TryResolveStructPathContext (path, this, out type, out start, reportErrors: false))
                return false;

            // Anonymous dynamic — no declared REFVAR layout
            if (type == null)
                return false;

            // Walk to the struct that owns the final field
            for (int i = start; i < path.Count - 1; i++) {
                if (path [i] == null)
                    return false;
                var field = type.FindField (path [i]);
                if (field == null || string.IsNullOrEmpty (field.structTypeName))
                    return false;
                if (field.structTypeName == "dynamic")
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

            context.ValidateStructFieldAccess (StructPathCodegen.PathNames (pathIdentifiers), this);
        }

        public override string typeName {
            get { return "struct field assignment"; }
        }
    }
}
