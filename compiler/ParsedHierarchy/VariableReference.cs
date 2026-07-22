using System.Collections.Generic;
using System.Linq;

namespace Ink.Parsed
{
    public class VariableReference : Expression
    {
        // - Normal variables have a single item in their "path"
        // - Knot/stitch names for read counts are actual dot-separated paths
        // - List names are dot separated: listName.itemName (or just itemName)
        // - Struct fields: instance.field or Type.static.field (self.field inside methods)
        public string name { get; private set; }

        public Identifier identifier {
            get {
                if (pathIdentifiers == null || pathIdentifiers.Count == 0) {
                    return null;
                }

                if( _singleIdentifier == null ) {
                    var name = string.Join (".", path.ToArray());
                    var firstDebugMetadata = pathIdentifiers.First().debugMetadata;
                    var debugMetadata = pathIdentifiers.Aggregate(firstDebugMetadata, (acc, id) => acc.Merge(id.debugMetadata));
                    _singleIdentifier = new Identifier { name = name, debugMetadata = debugMetadata };
                }
                
                return _singleIdentifier;
            }
        }
        Identifier _singleIdentifier;

        public List<Identifier> pathIdentifiers;
        public List<string> path { get; private set; }

        // Only known after GenerateIntoContainer has run
        public bool isConstantReference;
        public bool isListItemReference;
        public bool isStructReference;
        public bool isStructFieldReference;

        public Runtime.VariableReference runtimeVarRef { get { return _runtimeVarRef; } }

        public VariableReference (List<Identifier> pathIdentifiers)
        {
            this.pathIdentifiers = pathIdentifiers;
            this.path = pathIdentifiers.Select(id => id?.name).ToList();
            this.name = string.Join(".", pathIdentifiers);
        }

        public override void GenerateIntoContainer (Runtime.Container container)
        {
            Expression constantValue = null;

            // If it's a constant reference, just generate the literal expression value
            if ( story.constants.TryGetValue (name, out constantValue) ) {
                constantValue.GenerateConstantIntoContainer (container);
                isConstantReference = true;
                return;
            }

            // Struct type name alone → default instance variable
            if (path.Count == 1 && story.ResolveStruct (path [0]) != null) {
                isStructReference = true;
                _runtimeVarRef = new Runtime.VariableReference (path [0]);
                container.AddContent (_runtimeVarRef);
                return;
            }

            // Struct field path: root.field[.field...] (including self.field)
            if (path.Count >= 2 && IsStructFieldPath ()) {
                isStructFieldReference = true;
                // Use pointer for root so field sets through refs work; for get, ResolveStructInstance handles pointers
                if (path [0] == "self")
                    container.AddContent (new Runtime.VariablePointerValue ("self"));
                else
                    container.AddContent (new Runtime.VariableReference (path [0]));
                for (int i = 1; i < path.Count; i++) {
                    if (path [i] == "static" && i == 1)
                        continue;
                    container.AddContent (new Runtime.StructFieldGet (path [i]));
                }
                return;
            }

            _runtimeVarRef = new Runtime.VariableReference (name);

            // List item reference?
            if (path.Count == 1 || path.Count == 2) {
                string listItemName = null;
                string listName = null;

                if (path.Count == 1) listItemName = path [0];
                else {
                    listName = path [0];
                    listItemName = path [1];
                }

                var listItem = story.ResolveListItem (listName, listItemName, this);
                if (listItem) {
                    isListItemReference = true;
                }
            }

            container.AddContent (_runtimeVarRef);
        }

        bool IsStructFieldPath ()
        {
            if (path.Count < 2)
                return false;

            if (path [0] == "self")
                return true;

            // Type.static.field or Type.Instance.field
            if (story.ResolveStruct (path [0]) != null)
                return true;

            // Variable that may be struct-typed — only if it's an actual variable,
            // not a knot/stitch read-count path.
            var asFlowPath = new Path (pathIdentifiers);
            if (asFlowPath.ResolveFromContext (this) != null)
                return false;

            var listItem = story.ResolveListItem (path.Count >= 2 ? path [0] : null, path [path.Count - 1], this);
            if (listItem)
                return false;

            // Known global / temp variable root
            if (story.ResolveVariableWithName (path [0], this).found)
                return true;

            return false;
        }

        public override void ResolveReferences (Story context)
        {
            base.ResolveReferences (context);

            if (isConstantReference || isListItemReference || isStructReference || isStructFieldReference) {
                if (isStructFieldReference)
                    ValidateStructFieldPath (context);
                return;
            }

            // Is it a read count?
            var parsedPath = new Path (pathIdentifiers);
            Parsed.Object targetForCount = parsedPath.ResolveFromContext (this);
            if (targetForCount) {

                targetForCount.containerForCounting.visitsShouldBeCounted = true;

                if (_runtimeVarRef == null) return;

                _runtimeVarRef.pathForCount = targetForCount.runtimePath;
                _runtimeVarRef.name = null;

                var targetFlow = targetForCount as FlowBase;
                if (targetFlow && targetFlow.isFunction) {
                    if ( parent is Weave || parent is ContentList || parent is FlowBase) {
                        Warning ("'" + targetFlow.identifier + "' being used as read count rather than being called as function. Perhaps you intended to write " + targetFlow.name + "()");
                    }
                }

                return;
            }

            // Struct type?
            if (path.Count == 1 && context.ResolveStruct (path [0]) != null) {
                isStructReference = true;
                return;
            }

            // List item (also needed when this ref is only a struct-field default —
            // GenerateIntoContainer never runs for those, so the flag wouldn't be set there)
            if (path.Count == 1 || path.Count == 2) {
                string listItemName = null;
                string listName = null;
                if (path.Count == 1) {
                    listItemName = path [0];
                } else {
                    listName = path [0];
                    listItemName = path [1];
                }

                var listItem = context.ResolveListItem (listName, listItemName, this);
                if (listItem) {
                    isListItemReference = true;
                    return;
                }
            }

            if (path.Count > 1) {
                if (IsStructFieldPath ()) {
                    isStructFieldReference = true;
                    ValidateStructFieldPath (context);
                    return;
                }

                var errorMsg = "Could not find target for read count: " + parsedPath;
                if (path.Count <= 2)
                    errorMsg += ", or couldn't find list item with the name " + string.Join (",", path.ToArray());
                Error (errorMsg);
                return;
            }

            if (!context.ResolveVariableWithName (this.name, fromNode: this).found) {
                Error("Unresolved variable: "+this.ToString(), this);
            }
        }

        void ValidateStructFieldPath (Story context)
        {
            context.ValidateStructFieldAccess (path, this);
        }

        public override string ToString ()
        {
            return string.Join(".", path.ToArray());
        }

        Runtime.VariableReference _runtimeVarRef;
    }
}
