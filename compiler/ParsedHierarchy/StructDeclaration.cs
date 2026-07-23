using System.Collections.Generic;
using System.Linq;

namespace Ink.Parsed
{
    public class StructDeclaration : Parsed.Object, INamedContent
    {
        public Identifier identifier { get; set; }
        public string name => identifier?.name;
        public List<Identifier> baseTypes { get; protected set; }
        public bool isDynamic { get; set; }

        public List<VariableAssignment> ownFields { get; private set; }
        public List<Stitch> ownMethods { get; private set; }
        public List<Stitch> ownStitches { get; private set; }
        public List<ExternalDeclaration> ownExternals { get; private set; }

        // Flattened after linearization
        public List<StructFieldInfo> flattenedFields { get; private set; }
        public Dictionary<string, string> flattenedMethods { get; private set; }
        public Dictionary<string, string> baseCallPaths { get; private set; }
        public Dictionary<string, StructStitchInfo> flattenedStitches { get; private set; }
        public Dictionary<string, string> stitchBaseCallPaths { get; private set; }

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

        /// <summary>Flattened narrative stitch slot: divert path + author-facing signature.</summary>
        public class StructStitchInfo
        {
            public string path;
            public List<FlowBase.Argument> authorArguments;
        }

        public StructDeclaration (Identifier structName, List<Object> topLevelObjects, List<Identifier> baseTypes, bool isDynamic = false)
        {
            identifier = structName;
            this.baseTypes = baseTypes ?? new List<Identifier> ();
            this.isDynamic = isDynamic;

            ownFields = new List<VariableAssignment> ();
            ownMethods = new List<Stitch> ();
            ownStitches = new List<Stitch> ();
            ownExternals = new List<ExternalDeclaration> ();
            flattenedFields = new List<StructFieldInfo> ();
            flattenedMethods = new Dictionary<string, string> ();
            baseCallPaths = new Dictionary<string, string> ();
            flattenedStitches = new Dictionary<string, StructStitchInfo> ();
            stitchBaseCallPaths = new Dictionary<string, string> ();

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
                    EnsureSelfArgument (stitch);
                    if (stitch.isFunction)
                        ownMethods.Add (stitch);
                    else
                        ownStitches.Add (stitch);
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

        public static List<FlowBase.Argument> AuthorFacingArguments (Stitch stitch)
        {
            var result = new List<FlowBase.Argument> ();
            if (stitch?.arguments == null)
                return result;
            int start = 0;
            if (stitch.arguments.Count > 0 && stitch.arguments [0].identifier?.name == "self")
                start = 1;
            for (int i = start; i < stitch.arguments.Count; i++)
                result.Add (stitch.arguments [i]);
            return result;
        }

        public static bool SignaturesMatch (List<FlowBase.Argument> a, List<FlowBase.Argument> b)
        {
            if (a == null) a = new List<FlowBase.Argument> ();
            if (b == null) b = new List<FlowBase.Argument> ();
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++) {
                if (a [i].isByReference != b [i].isByReference)
                    return false;
                if (a [i].isDivertTarget != b [i].isDivertTarget)
                    return false;
                if ((a [i].structTypeName ?? "") != (b [i].structTypeName ?? ""))
                    return false;
            }
            return true;
        }

        public override Runtime.Object GenerateRuntimeObject ()
        {
            story.AddStructDeclaration (this);

            // TypeName.static.MemberName containers
            runtimeTypeContainer = new Runtime.Container ();
            runtimeTypeContainer.name = name;

            var staticContainer = new Runtime.Container ();
            staticContainer.name = "static";

            foreach (var method in ownMethods) {
                var methodRuntime = method.runtimeObject as Runtime.Container;
                if (methodRuntime != null)
                    staticContainer.AddToNamedContentOnly (methodRuntime);
            }

            foreach (var stitch in ownStitches) {
                var stitchRuntime = stitch.runtimeObject as Runtime.Container;
                if (stitchRuntime != null)
                    staticContainer.AddToNamedContentOnly (stitchRuntime);
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
            var stitches = new Dictionary<string, StructStitchInfo> ();
            var stitchBaseCalls = new Dictionary<string, string> ();

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

                // struct cannot inherit dynamic; dynamic may inherit struct or dynamic
                if (!isDynamic && baseStruct.isDynamic) {
                    Error ("Struct '" + name + "' cannot inherit dynamic '" + baseName + "'", this);
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
                foreach (var kv in baseStruct.flattenedStitches) {
                    if (!stitches.ContainsKey (kv.Key))
                        stitches [kv.Key] = CloneStitchInfo (kv.Value);
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

                if (stitches.ContainsKey (methodName) || ownStitches.Any (s => s.name == methodName)) {
                    Error ("'" + methodName + "' cannot be both a method and a stitch on struct '" + name + "'", method);
                    continue;
                }

                string inheritedPath;
                if (methods.TryGetValue (methodName, out inheritedPath))
                    baseCalls [methodName] = inheritedPath;

                methods [methodName] = name + ".static." + methodName;
            }

            foreach (var stitch in ownStitches) {
                var stitchName = stitch.name;
                if (stitchName == "static" || stitchName == "self" || stitchName == "base") {
                    Error ("'" + stitchName + "' is reserved and cannot be used as a stitch name", stitch);
                    continue;
                }

                if (methods.ContainsKey (stitchName) || ownMethods.Any (m => m.name == stitchName)) {
                    Error ("'" + stitchName + "' cannot be both a method and a stitch on struct '" + name + "'", stitch);
                    continue;
                }

                var authorArgs = AuthorFacingArguments (stitch);

                StructStitchInfo inherited;
                if (stitches.TryGetValue (stitchName, out inherited)) {
                    if (!SignaturesMatch (authorArgs, inherited.authorArguments)) {
                        Error ("Stitch '" + stitchName + "' overrides inherited stitch with a different signature", stitch);
                        continue;
                    }
                    stitchBaseCalls [stitchName] = inherited.path;
                }

                stitches [stitchName] = new StructStitchInfo {
                    path = name + ".static." + stitchName,
                    authorArguments = authorArgs
                };
            }

            flattenedFields = fields.Values.ToList ();
            flattenedMethods = methods;
            baseCallPaths = baseCalls;
            flattenedStitches = stitches;
            stitchBaseCallPaths = stitchBaseCalls;
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

        public bool HasStitch (string stitchName)
        {
            return stitchName != null && flattenedStitches != null && flattenedStitches.ContainsKey (stitchName);
        }

        public StructStitchInfo FindStitch (string stitchName)
        {
            if (stitchName == null || flattenedStitches == null)
                return null;
            StructStitchInfo info;
            return flattenedStitches.TryGetValue (stitchName, out info) ? info : null;
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

        static StructStitchInfo CloneStitchInfo (StructStitchInfo src)
        {
            return new StructStitchInfo {
                path = src.path,
                authorArguments = src.authorArguments != null
                    ? new List<FlowBase.Argument> (src.authorArguments)
                    : new List<FlowBase.Argument> ()
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

            var stitchPaths = new Dictionary<string, string> ();
            foreach (var kv in flattenedStitches)
                stitchPaths [kv.Key] = kv.Value.path;

            var bases = baseTypes.Select (b => b?.name).Where (n => n != null).ToList ();
            runtimeStructDef = new Runtime.StructDeclaration (
                name,
                bases,
                runtimeFields,
                new Dictionary<string, string> (flattenedMethods),
                new Dictionary<string, string> (baseCallPaths),
                stitchPaths,
                new Dictionary<string, string> (stitchBaseCallPaths),
                isDynamic ? Runtime.StructKind.Dynamic : Runtime.StructKind.Struct
            );
            return runtimeStructDef;
        }

        public override void ResolveReferences (Story context)
        {
            base.ResolveReferences (context);

            context.CheckForNamingCollisions (this, identifier, Story.SymbolType.Struct);

            // Field vs member name collisions within this type
            foreach (var field in ownFields) {
                if (ownMethods.Any (m => m.name == field.variableName))
                    Error ("Field '" + field.variableName + "' conflicts with a method of the same name", field);
                if (ownStitches.Any (s => s.name == field.variableName))
                    Error ("Field '" + field.variableName + "' conflicts with a stitch of the same name", field);
            }
        }

        public override string typeName {
            get { return isDynamic ? "Dynamic" : "Struct"; }
        }
    }
}
