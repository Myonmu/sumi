using System.Collections.Generic;
using System.Linq;

namespace Ink.Parsed
{
    public class StructDeclaration : Parsed.Object, INamedContent
    {
        public Identifier identifier { get; set; }
        public string name => identifier?.name;
        public List<Identifier> baseTypes { get; protected set; }

        public List<VariableAssignment> ownFields { get; private set; }
        public List<Stitch> ownMethods { get; private set; }
        public List<ExternalDeclaration> ownExternals { get; private set; }

        // Flattened after linearization
        public List<StructFieldInfo> flattenedFields { get; private set; }
        public Dictionary<string, string> flattenedMethods { get; private set; }
        public Dictionary<string, string> baseCallPaths { get; private set; }

        public Runtime.Container runtimeTypeContainer { get; private set; }
        public Runtime.StructDeclaration runtimeStructDef { get; private set; }

        public class StructFieldInfo
        {
            public string name;
            public bool isRefVar;
            public string structTypeName; // null if scalar
            public Expression defaultExpression;
            public Runtime.Object runtimeDefault; // filled during codegen
            public VariableAssignment sourceDecl;
        }

        public StructDeclaration (Identifier structName, List<Object> topLevelObjects, List<Identifier> baseTypes)
        {
            identifier = structName;
            this.baseTypes = baseTypes ?? new List<Identifier> ();

            ownFields = new List<VariableAssignment> ();
            ownMethods = new List<Stitch> ();
            ownExternals = new List<ExternalDeclaration> ();
            flattenedFields = new List<StructFieldInfo> ();
            flattenedMethods = new Dictionary<string, string> ();
            baseCallPaths = new Dictionary<string, string> ();

            if (topLevelObjects == null)
                topLevelObjects = new List<Object> ();

            // Flatten weave wrappers: knot-level content may include Text and Weave
            var flat = new List<Object> ();
            FlattenContent (topLevelObjects, flat);

            foreach (var obj in flat) {
                var varAss = obj as VariableAssignment;
                if (varAss != null && varAss.isDeclaration) {
                    varAss.isGlobalDeclaration = false;
                    varAss.isStructField = true;
                    ownFields.Add (varAss);
                    AddContent (varAss);
                    continue;
                }

                var stitch = obj as Stitch;
                if (stitch != null) {
                    if (!stitch.isFunction) {
                        // Will error in ResolveReferences
                    }
                    ownMethods.Add (stitch);
                    // Prepend implicit ref self
                    EnsureSelfArgument (stitch);
                    AddContent (stitch);
                    continue;
                }

                var ext = obj as ExternalDeclaration;
                if (ext != null) {
                    ownExternals.Add (ext);
                    AddContent (ext);
                    continue;
                }

                // Skip pure whitespace/newlines; flag unexpected content later
                var text = obj as Text;
                if (text != null)
                    continue;

                AddContent (obj);
            }
        }

        static void FlattenContent (List<Object> source, List<Object> dest)
        {
            foreach (var obj in source) {
                var weave = obj as Weave;
                if (weave != null && weave.content != null) {
                    FlattenContent (weave.content, dest);
                } else {
                    dest.Add (obj);
                }
            }
        }

        static void EnsureSelfArgument (Stitch method)
        {
            if (method.arguments == null)
                method.arguments = new List<FlowBase.Argument> ();

            // Don't double-add
            if (method.arguments.Count > 0 && method.arguments [0].identifier?.name == "self")
                return;

            var selfArg = new FlowBase.Argument {
                identifier = new Identifier { name = "self" },
                isByReference = true,
                isDivertTarget = false
            };
            method.arguments.Insert (0, selfArg);
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            story.AddStructDeclaration (this);

            // TypeName.static.MethodName containers
            runtimeTypeContainer = new Runtime.Container ();
            runtimeTypeContainer.name = name;

            var staticContainer = new Runtime.Container ();
            staticContainer.name = "static";

            foreach (var method in ownMethods) {
                if (!method.isFunction) {
                    Error ("Struct methods must be declared as function stitches: = function " + method.name + " =", method);
                    continue;
                }
                var methodRuntime = method.runtimeObject as Runtime.Container;
                if (methodRuntime != null)
                    staticContainer.AddToNamedContentOnly (methodRuntime);
            }

            runtimeTypeContainer.AddToNamedContentOnly (staticContainer);

            // Externals register themselves when generated
            foreach (var ext in ownExternals) {
                var _ = ext.runtimeObject;
            }

            // Must return non-null so Parsed.Object caches generation. Returning null caused
            // GenerateRuntimeObject to run twice (weave + Story.ExportRuntime), which
            // re-registered EXTERNAL declarations as duplicates.
            // Weave skips adding this container as narrative content.
            return runtimeTypeContainer;
        }

        public void BuildLinearization (Dictionary<string, StructDeclaration> allStructs)
        {
            if (_linearized)
                return;

            var fields = new Dictionary<string, StructFieldInfo> ();
            var methods = new Dictionary<string, string> ();
            var baseCalls = new Dictionary<string, string> ();

            // Detect cycles
            if (HasBaseCycle (allStructs, new HashSet<string> ())) {
                Error ("Struct inheritance cycle detected involving '" + name + "'");
                _linearized = true;
                return;
            }

            foreach (var baseId in baseTypes) {
                var baseName = baseId?.name;
                if (string.IsNullOrEmpty (baseName))
                    continue;

                StructDeclaration baseStruct;
                if (!allStructs.TryGetValue (baseName, out baseStruct)) {
                    Error ("Unknown base struct '" + baseName + "'", this);
                    continue;
                }

                baseStruct.BuildLinearization (allStructs);

                foreach (var field in baseStruct.flattenedFields) {
                    if (!fields.ContainsKey (field.name))
                        fields [field.name] = CloneFieldInfo (field);
                }
                foreach (var kv in baseStruct.flattenedMethods) {
                    if (!methods.ContainsKey (kv.Key))
                        methods [kv.Key] = kv.Value;
                }
            }

            // Child overrides
            foreach (var fieldDecl in ownFields) {
                var info = FieldInfoFromDecl (fieldDecl);
                if (info.name == "static" || info.name == "self" || info.name == "base") {
                    Error ("'" + info.name + "' is reserved and cannot be used as a field name", fieldDecl);
                    continue;
                }
                fields [info.name] = info;
            }

            foreach (var method in ownMethods) {
                var methodName = method.name;
                if (methodName == "static" || methodName == "self" || methodName == "base") {
                    Error ("'" + methodName + "' is reserved and cannot be used as a method name", method);
                    continue;
                }

                string inheritedPath;
                if (methods.TryGetValue (methodName, out inheritedPath))
                    baseCalls [methodName] = inheritedPath;

                methods [methodName] = name + ".static." + methodName;
            }

            flattenedFields = fields.Values.ToList ();
            flattenedMethods = methods;
            baseCallPaths = baseCalls;
            _linearized = true;
        }

        public StructFieldInfo FindField (string fieldName)
        {
            if (fieldName == null || flattenedFields == null)
                return null;
            foreach (var field in flattenedFields) {
                if (field.name == fieldName)
                    return field;
            }
            return null;
        }

        public bool HasMethod (string methodName)
        {
            return methodName != null && flattenedMethods != null && flattenedMethods.ContainsKey (methodName);
        }

        bool _linearized;

        bool HasBaseCycle (Dictionary<string, StructDeclaration> allStructs, HashSet<string> visiting)
        {
            if (!visiting.Add (name))
                return true;
            foreach (var baseId in baseTypes) {
                StructDeclaration baseStruct;
                if (baseId?.name != null && allStructs.TryGetValue (baseId.name, out baseStruct)) {
                    if (baseStruct.HasBaseCycle (allStructs, visiting))
                        return true;
                }
            }
            visiting.Remove (name);
            return false;
        }

        static StructFieldInfo CloneFieldInfo (StructFieldInfo src)
        {
            return new StructFieldInfo {
                name = src.name,
                isRefVar = src.isRefVar,
                structTypeName = src.structTypeName,
                defaultExpression = src.defaultExpression,
                sourceDecl = src.sourceDecl
            };
        }

        StructFieldInfo FieldInfoFromDecl (VariableAssignment decl)
        {
            return new StructFieldInfo {
                name = decl.variableName,
                isRefVar = decl.isRefVar,
                structTypeName = decl.structTypeName,
                defaultExpression = decl.expression,
                sourceDecl = decl
            };
        }

        public Runtime.StructDeclaration BuildRuntimeStructDef ()
        {
            var runtimeFields = new List<Runtime.StructFieldSlot> ();
            foreach (var field in flattenedFields) {
                var kind = field.isRefVar ? Runtime.StructFieldKind.RefVar : Runtime.StructFieldKind.Var;
                runtimeFields.Add (new Runtime.StructFieldSlot (
                    field.name,
                    kind,
                    field.structTypeName,
                    field.runtimeDefault
                ));
            }

            var bases = baseTypes.Select (b => b?.name).Where (n => n != null).ToList ();
            runtimeStructDef = new Runtime.StructDeclaration (
                name,
                bases,
                runtimeFields,
                new Dictionary<string, string> (flattenedMethods),
                new Dictionary<string, string> (baseCallPaths)
            );
            return runtimeStructDef;
        }

        public override void ResolveReferences (Story context)
        {
            base.ResolveReferences (context);

            context.CheckForNamingCollisions (this, identifier, Story.SymbolType.Struct);

            foreach (var method in ownMethods) {
                if (!method.isFunction)
                    Error ("Only function stitches are allowed inside structs (use '= function " + method.name + " =')", method);
            }
        }

        public override string typeName {
            get { return "Struct"; }
        }
    }
}
