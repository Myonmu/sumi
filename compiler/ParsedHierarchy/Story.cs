using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using System.Diagnostics;

[assembly: InternalsVisibleTo("tests")]

namespace Ink.Parsed
{
	public class Story : FlowBase
    {
        public override FlowLevel flowLevel { get { return FlowLevel.Story; } }

        /// <summary>
        /// Had error during code gen, resolve references?
        /// Most of the time it shouldn't be necessary to use this
        /// since errors should be caught by the error handler.
        /// </summary>
        internal bool hadError { get { return _hadError; } }
        internal bool hadWarning { get { return _hadWarning; } }

        public Dictionary<string, Expression> constants;
        public Dictionary<string, ExternalDeclaration> externals;
        public Dictionary<string, StructDeclaration> structs = new Dictionary<string, StructDeclaration> ();

        // Build setting for exporting:
        // When true, the visit count for *all* knots, stitches, choices,
        // and gathers is counted. When false, only those that are direclty
        // referenced by the ink are recorded. Use this flag to allow game-side
        // querying of  arbitrary knots/stitches etc.
        // Storing all counts is more robust and future proof (updates to the story file
        // that reference previously uncounted visits are possible, but generates a much
        // larger safe file, with a lot of potentially redundant counts.
        public bool countAllVisits = false;

        public Story (List<Parsed.Object> toplevelObjects, bool isInclude = false) : base(null, toplevelObjects, isIncludedStory:isInclude)
		{
            // Don't do anything much on construction, leave it lightweight until
            // the ExportRuntime method is called.
		}

        // Before this function is called, we have IncludedFile objects interspersed
        // in our content wherever an include statement was.
        // So that the include statement can be added in a sensible place (e.g. the
        // top of the file) without side-effects of jumping into a knot that was
        // defined in that include, we separate knots and stitches from anything
        // else defined at the top scope of the included file.
        //
        // Algorithm: For each IncludedFile we find, split its contents into
        // knots/stiches and any other content. Insert the normal content wherever
        // the include statement was, and append the knots/stitches to the very
        // end of the main story.
        protected override void PreProcessTopLevelObjects(List<Parsed.Object> topLevelContent)
        {
            var flowsFromOtherFiles = new List<FlowBase> ();

            // Inject included files
            int i = 0;
            while (i < topLevelContent.Count) {
                var obj = topLevelContent [i];
                if (obj is IncludedFile) {

                    var file = (IncludedFile)obj;

                    // Remove the IncludedFile itself
                    topLevelContent.RemoveAt (i);

                    // When an included story fails to load, the include
                    // line itself is still valid, so we have to handle it here
                    if (file.includedStory) {

                        var nonFlowContent = new List<Parsed.Object> ();

                        var subStory = file.includedStory;

                        // Allow empty file
                        if (subStory.content != null) {

                            foreach (var subStoryObj in subStory.content) {
                                if (subStoryObj is FlowBase) {
                                    flowsFromOtherFiles.Add ((FlowBase)subStoryObj);
                                } else {
                                    nonFlowContent.Add (subStoryObj);
                                }
                            }

                            // Add newline on the end of the include
                            nonFlowContent.Add (new Parsed.Text ("\n"));

                            // Add contents of the file in its place
                            topLevelContent.InsertRange (i, nonFlowContent);

                            // Skip past the content of this sub story
                            // (since it will already have recursively included
                            //  any lines from other files)
                            i += nonFlowContent.Count;
                        }

                    }

                    // Include object has been removed, with possible content inserted,
                    // and position of 'i' will have been determined already.
                    continue;
                }

                // Non-include: skip over it
                else {
                    i++;
                }
            }

            // Add the flows we collected from the included files to the
            // end of our list of our content
            topLevelContent.AddRange (flowsFromOtherFiles.ToArray());

        }

        public Runtime.Story ExportRuntime(ErrorHandler errorHandler = null)
		{
            _errorHandler = errorHandler;

            // Find all constants before main export begins, so that VariableReferences know
            // whether to generate a runtime variable reference or the literal value
            constants = new Dictionary<string, Expression> ();
            foreach (var constDecl in FindAll<ConstantDeclaration> ()) {

                // Check for duplicate definitions
                Parsed.Expression existingDefinition = null;
                if (constants.TryGetValue (constDecl.constantName, out existingDefinition)) {
                    if (!existingDefinition.Equals (constDecl.expression)) {
                        var errorMsg = string.Format ("CONST '{0}' has been redefined with a different value. Multiple definitions of the same CONST are valid so long as they contain the same value. Initial definition was on {1}.", constDecl.constantName, existingDefinition.debugMetadata);
                        Error (errorMsg, constDecl, isWarning:false);
                    }
                }

                constants [constDecl.constantName] = constDecl.expression;
            }

            // List definitions are treated like constants too - they should be usable
            // from other variable declarations.
            _listDefs = new Dictionary<string, ListDefinition> ();
            foreach (var listDef in FindAll<ListDefinition> ()) {
                _listDefs [listDef.identifier?.name] = listDef;
            }

            externals = new Dictionary<string, ExternalDeclaration> ();

            // Collect structs early so VariableReferences can resolve type names
            structs = new Dictionary<string, StructDeclaration> ();
            foreach (var structDecl in FindAll<StructDeclaration> ()) {
                AddStructDeclaration (structDecl);
            }

            // Linearize inheritance before codegen
            foreach (var kv in structs)
                kv.Value.BuildLinearization (structs);

            // Resolution of weave point names has to come first, before any runtime code generation
            // since names have to be ready before diverts start getting created.
            // (It used to be done in the constructor for a weave, but didn't allow us to generate
            // errors when name resolution failed.)
            ResolveWeavePointNaming ();

            // Get default implementation of runtimeObject, which calls ContainerBase's generation method
            var rootContainer = runtimeObject as Runtime.Container;

            // Attach struct type containers (Type.static.Method) as named content only
            foreach (var kv in structs) {
                var structDecl = kv.Value;
                // Force generation (registers + builds type container)
                var _ = structDecl.runtimeObject;
                if (structDecl.runtimeTypeContainer != null)
                    rootContainer.AddToNamedContentOnly (structDecl.runtimeTypeContainer);
            }

            // Export initialisation of global variables
            // TODO: We *could* add this as a declarative block to the story itself...
            var variableInitialisation = new Runtime.Container ();
            variableInitialisation.AddContent (Runtime.ControlCommand.EvalStart ());

            // Materialise default instances for each struct type as TypeName (and TypeName.static alias via same value)
            var runtimeStructs = new List<Runtime.StructDeclaration> ();
            foreach (var kv in structs) {
                var structDecl = kv.Value;
                MaterialiseStructFieldDefaults (structDecl, variableInitialisation);

                var runtimeDef = structDecl.BuildRuntimeStructDef ();
                runtimeStructs.Add (runtimeDef);

                // Default instance global named after the type
                variableInitialisation.AddContent (new Runtime.StructCreateDefault (structDecl.name));
                var defaultAss = new Runtime.VariableAssignment (structDecl.name, isNewDeclaration: true);
                defaultAss.isGlobal = true;
                variableInitialisation.AddContent (defaultAss);
            }

            // Global variables are those that are local to the story and marked as global
            var runtimeLists = new List<Runtime.ListDefinition> ();
            foreach (var nameDeclPair in variableDeclarations) {
                var varName = nameDeclPair.Key;
                var varDecl = nameDeclPair.Value;
                if (varDecl.isGlobalDeclaration) {

                    if (varDecl.listDefinition != null) {
                        _listDefs[varName] = varDecl.listDefinition;
                        variableInitialisation.AddContent (varDecl.listDefinition.runtimeObject);
                        runtimeLists.Add (varDecl.listDefinition.runtimeListDefinition);
                    } else if (varDecl.structTypeName != null) {
                        GenerateStructVariableInit (varDecl, variableInitialisation);
                    } else {
                        varDecl.expression.GenerateIntoContainer (variableInitialisation);
                    }

                    var runtimeVarAss = new Runtime.VariableAssignment (varName, isNewDeclaration:true);
                    runtimeVarAss.isGlobal = true;
                    variableInitialisation.AddContent (runtimeVarAss);
                }
            }

            variableInitialisation.AddContent (Runtime.ControlCommand.EvalEnd ());
            variableInitialisation.AddContent (Runtime.ControlCommand.End ());

            if (variableDeclarations.Count > 0 || structs.Count > 0) {
                variableInitialisation.name = "global decl";
                rootContainer.AddToNamedContentOnly (variableInitialisation);
            }

            // Signal that it's safe to exit without error, even if there are no choices generated
            // (this only happens at the end of top level content that isn't in any particular knot)
            rootContainer.AddContent (Runtime.ControlCommand.Done ());

			// Replace runtimeObject with Story object instead of the Runtime.Container generated by Parsed.ContainerBase
            var runtimeStory = new Runtime.Story (rootContainer, runtimeLists, runtimeStructs);

			runtimeObject = runtimeStory;

            if (_hadError)
                return null;

            // Optimisation step - inline containers that can be
            FlattenContainersIn (rootContainer);

			// Now that the story has been fulled parsed into a hierarchy,
			// and the derived runtime hierarchy has been built, we can
			// resolve referenced symbols such as variables and paths.
			// e.g. for paths " -> knotName --> stitchName" into an INKPath (knotName.stitchName)
			// We don't make any assumptions that the INKPath follows the same
			// conventions as the script format, so we resolve to actual objects before
			// translating into an INKPath. (This also allows us to choose whether
			// we want the paths to be absolute)
			ResolveReferences (this);

            if (_hadError)
                return null;

            runtimeStory.ResetState ();

			return runtimeStory;
		}

        public ListDefinition ResolveList (string listName)
        {
            ListDefinition list;
            if (!_listDefs.TryGetValue (listName, out list))
                return null;
            return list;
        }

        public ListElementDefinition ResolveListItem (string listName, string itemName, Parsed.Object source = null)
        {
            ListDefinition listDef = null;

            // Search a specific list if we know its name (i.e. the form listName.itemName)
            if (listName != null) {
                if (!_listDefs.TryGetValue (listName, out listDef))
                    return null;

                return listDef.ItemNamed (itemName);
            }

            // Otherwise, try to search all lists
            else {

                ListElementDefinition foundItem = null;
                ListDefinition originalFoundList = null;

                foreach (var namedList in _listDefs) {
                    var listToSearch = namedList.Value;
                    var itemInThisList = listToSearch.ItemNamed (itemName);
                    if (itemInThisList) {
                        if (foundItem != null) {
                            Error ("Ambiguous item name '" + itemName + "' found in multiple sets, including "+originalFoundList.identifier+" and "+listToSearch.identifier, source, isWarning:false);
                        } else {
                            foundItem = itemInThisList;
                            originalFoundList = listToSearch;
                        }
                    }
                }

                return foundItem;
            }
        }

        void FlattenContainersIn (Runtime.Container container)
        {
            // Need to create a collection to hold the inner containers
            // because otherwise we'd end up modifying during iteration
            var innerContainers = new HashSet<Runtime.Container> ();

            foreach (var c in container.content) {
                var innerContainer = c as Runtime.Container;
                if (innerContainer)
                    innerContainers.Add (innerContainer);
            }

            // Can't flatten the named inner containers, but we can at least
            // iterate through their children
            if (container.namedContent != null) {
                foreach (var keyValue in container.namedContent) {
                    var namedInnerContainer = keyValue.Value as Runtime.Container;
                    if (namedInnerContainer)
                        innerContainers.Add (namedInnerContainer);
                }
            }

            foreach (var innerContainer in innerContainers) {
                TryFlattenContainer (innerContainer);
                FlattenContainersIn (innerContainer);
            }
        }

        void TryFlattenContainer (Runtime.Container container)
        {
            if (container.namedContent.Count > 0 || container.hasValidName || _dontFlattenContainers.Contains(container))
                return;

            // Inline all the content in container into the parent
            var parentContainer = container.parent as Runtime.Container;
            if (parentContainer) {

                var contentIdx = parentContainer.content.IndexOf (container);
                parentContainer.content.RemoveAt (contentIdx);

                var dm = container.ownDebugMetadata;

                foreach (var innerContent in container.content) {
                    innerContent.parent = null;
                    if (dm != null && innerContent.ownDebugMetadata == null)
                        innerContent.debugMetadata = dm;
                    parentContainer.InsertContent (innerContent, contentIdx);
                    contentIdx++;
                }
            }
        }

        public override void Error(string message, Parsed.Object source, bool isWarning)
		{
            ErrorType errorType = isWarning ? ErrorType.Warning : ErrorType.Error;

            var sb = new StringBuilder ();
            if (source is AuthorWarning) {
                sb.Append ("TODO: ");
                errorType = ErrorType.Author;
            } else if (isWarning) {
                sb.Append ("WARNING: ");
            } else {
                sb.Append ("ERROR: ");
            }

            if (source && source.debugMetadata != null && source.debugMetadata.startLineNumber >= 1 ) {

                if (source.debugMetadata.fileName != null) {
                    sb.AppendFormat ("'{0}' ", source.debugMetadata.fileName);
                }

                sb.AppendFormat ("line {0}: ", source.debugMetadata.startLineNumber);
            }

            sb.Append (message);

            message = sb.ToString ();

            if (_errorHandler != null) {
                _hadError = errorType == ErrorType.Error;
                _hadWarning = errorType == ErrorType.Warning;
                _errorHandler (message, errorType);
            } else {
                throw new System.Exception (message);
            }
		}

        public void ResetError()
        {
            _hadError = false;
            _hadWarning = false;
        }

        public bool IsExternal(string namedFuncTarget)
        {
            return externals.ContainsKey (namedFuncTarget);
        }

        public void AddExternal(ExternalDeclaration decl)
        {
            ExternalDeclaration existing;
            if (externals.TryGetValue (decl.name, out existing)) {
                // Same declaration re-generated (null-returning runtimeObject is not cached) — ok
                if (existing != decl)
                    Error ("Duplicate EXTERNAL definition of '"+decl.name+"'", decl, false);
            } else {
                externals [decl.name] = decl;
            }
        }

        public void AddStructDeclaration(StructDeclaration decl)
        {
            if (decl == null || decl.name == null)
                return;

            StructDeclaration existing;
            if (structs.TryGetValue (decl.name, out existing)) {
                if (existing != decl)
                    Error ("Duplicate STRUCT definition of '" + decl.name + "'", decl, false);
                return;
            }
            structs [decl.name] = decl;
        }

        void MaterialiseStructFieldDefaults (StructDeclaration structDecl, Runtime.Container unusedInitContainer)
        {
            foreach (var field in structDecl.flattenedFields) {
                if (field.isRefVar) {
                    var varRef = field.defaultExpression as VariableReference;
                    if (varRef != null && varRef.name == "none") {
                        field.runtimeDefault = new Runtime.StructRefValue (null);
                    } else if (varRef != null) {
                        field.runtimeDefault = new Runtime.StructRefValue (varRef.name);
                    } else {
                        field.runtimeDefault = new Runtime.StructRefValue (null);
                    }
                    continue;
                }

                if (field.defaultExpression == null) {
                    field.runtimeDefault = null;
                    continue;
                }

                var num = field.defaultExpression as Number;
                if (num != null) {
                    if (num.value is bool)
                        field.runtimeDefault = new Runtime.BoolValue ((bool)num.value);
                    else if (num.value is int)
                        field.runtimeDefault = new Runtime.IntValue ((int)num.value);
                    else if (num.value is float)
                        field.runtimeDefault = new Runtime.FloatValue ((float)num.value);
                    else
                        field.runtimeDefault = Runtime.Value.Create (num.value);
                    continue;
                }

                var strExpr = field.defaultExpression as StringExpression;
                if (strExpr != null && strExpr.isSingleString) {
                    field.runtimeDefault = new Runtime.StringValue (strExpr.ToString ());
                    continue;
                }

                // Struct-typed default referring to a type/instance — resolved at instance creation
                field.runtimeDefault = null;
            }
        }

        void GenerateStructVariableInit (VariableAssignment varDecl, Runtime.Container container)
        {
            // Global REFVAR: store a reference identity, not an owned instance
            if (varDecl.isRefVar) {
                var varRef = varDecl.expression as VariableReference;
                if (varRef != null && varRef.name == "none") {
                    container.AddContent (new Runtime.StructRefValue (null));
                } else if (varDecl.expression is NoneLiteral) {
                    container.AddContent (new Runtime.StructRefValue (null));
                } else if (varRef != null && varRef.path != null && varRef.path.Count == 1) {
                    container.AddContent (new Runtime.StructRefValue (varRef.name));
                } else if (varDecl.expression == null) {
                    container.AddContent (new Runtime.StructRefValue (null));
                } else {
                    Error ("REFVAR '" + varDecl.variableName + "' must be initialised to a global instance name or none", varDecl, false);
                    container.AddContent (new Runtime.StructRefValue (null));
                }
                return;
            }

            if (varDecl.expression != null) {
                varDecl.expression.GenerateIntoContainer (container);
                // Deep-copy on assign handled by VariablesState for StructValue
            } else {
                container.AddContent (new Runtime.StructCreateDefault (varDecl.structTypeName));
            }
        }

        public StructDeclaration ResolveStruct (string structName)
        {
            if (structName == null || structs == null)
                return null;
            StructDeclaration decl;
            if (structs.TryGetValue (structName, out decl))
                return decl;
            return null;
        }

        public VariableAssignment ResolveVariableDeclaration (string varName, Parsed.Object fromNode)
        {
            if (varName == null)
                return null;

            var ownerFlow = fromNode == null ? this : fromNode.ClosestFlowBase ();
            if (ownerFlow != null && ownerFlow != this
                && ownerFlow.variableDeclarations != null
                && ownerFlow.variableDeclarations.ContainsKey (varName)) {
                return ownerFlow.variableDeclarations [varName];
            }

            VariableAssignment globalDecl;
            if (variableDeclarations.TryGetValue (varName, out globalDecl))
                return globalDecl;

            return null;
        }

        /// <summary>
        /// Struct type of a variable, temp, or typed function parameter in scope.
        /// </summary>
        public string ResolveStructTypeNameForName (string varName, Parsed.Object fromNode)
        {
            if (varName == null)
                return null;

            if (varName == "self") {
                var selfStruct = ClosestStructContext (fromNode);
                return selfStruct?.name;
            }

            // Innermost flow first: arguments, then temps, then outer flows, then globals
            var flow = fromNode == null ? this : fromNode.ClosestFlowBase ();
            while (flow != null) {
                if (flow.arguments != null) {
                    foreach (var arg in flow.arguments) {
                        if (arg.identifier?.name == varName && arg.structTypeName != null)
                            return arg.structTypeName;
                    }
                }

                VariableAssignment localDecl;
                if (flow.variableDeclarations != null
                    && flow.variableDeclarations.TryGetValue (varName, out localDecl)
                    && localDecl.structTypeName != null) {
                    return localDecl.structTypeName;
                }

                if (flow is Story)
                    break;
                var parentObj = flow.parent;
                flow = parentObj != null ? parentObj.ClosestFlowBase () : null;
            }

            // Story globals (in case the parent walk did not reach the story flow)
            VariableAssignment globalDecl;
            if (variableDeclarations != null
                && variableDeclarations.TryGetValue (varName, out globalDecl)
                && globalDecl.structTypeName != null) {
                return globalDecl.structTypeName;
            }

            return null;
        }

        /// <summary>True if actualType is expectedType or inherits from it.</summary>
        public bool StructTypeIsCompatible (string actualTypeName, string expectedTypeName)
        {
            if (actualTypeName == null || expectedTypeName == null)
                return false;
            if (actualTypeName == expectedTypeName)
                return true;

            var actual = ResolveStruct (actualTypeName);
            if (actual == null)
                return false;

            var visiting = new HashSet<string> ();
            var queue = new Queue<StructDeclaration> ();
            queue.Enqueue (actual);
            while (queue.Count > 0) {
                var type = queue.Dequeue ();
                if (!visiting.Add (type.name))
                    continue;
                if (type.name == expectedTypeName)
                    return true;
                if (type.baseTypes == null)
                    continue;
                foreach (var baseId in type.baseTypes) {
                    var baseStruct = ResolveStruct (baseId?.name);
                    if (baseStruct != null)
                        queue.Enqueue (baseStruct);
                }
            }
            return false;
        }

        public static StructDeclaration ClosestStructContext (Parsed.Object fromNode)
        {
            var ancestor = fromNode;
            while (ancestor != null) {
                var stitch = ancestor as Stitch;
                if (stitch != null && stitch.isFunction && ancestor.parent is StructDeclaration)
                    return (StructDeclaration)ancestor.parent;
                ancestor = ancestor.parent;
            }
            return null;
        }

        /// <summary>
        /// Resolve the static struct type for a dotted receiver / field path.
        /// memberStartIndex is the first field (or method) component after the root / Type.static / Type.Instance prefix.
        /// Returns false when the path is not a struct member path.
        /// </summary>
        public bool TryResolveStructPathContext (IList<string> path, Parsed.Object fromNode, out StructDeclaration type, out int memberStartIndex, bool reportErrors = true)
        {
            type = null;
            memberStartIndex = 1;

            if (path == null || path.Count < 1)
                return false;

            string root = path [0];

            if (root == "self") {
                type = ClosestStructContext (fromNode);
                if (type == null) {
                    if (reportErrors)
                        fromNode.Error ("'self' is only valid inside a struct method", fromNode);
                    return false;
                }
                memberStartIndex = 1;
                return true;
            }

            if (root == "base")
                return false;

            var asStructType = ResolveStruct (root);
            if (asStructType != null) {
                type = asStructType;
                memberStartIndex = 1;
                if (path.Count > 1 && path [1] == "static") {
                    memberStartIndex = 2;
                } else if (path.Count > 1 && asStructType.FindField (path [1]) == null) {
                    // Type.Instance.field — instance is a global/temp of a compatible struct type
                    var instDecl = ResolveVariableDeclaration (path [1], fromNode);
                    if (instDecl != null && instDecl.structTypeName != null) {
                        var instType = ResolveStruct (instDecl.structTypeName);
                        if (instType != null)
                            type = instType;
                        memberStartIndex = 2;
                    }
                }
                return true;
            }

            var varDecl = ResolveVariableDeclaration (root, fromNode);
            if (varDecl != null && varDecl.structTypeName != null) {
                type = ResolveStruct (varDecl.structTypeName);
                if (type == null) {
                    if (reportErrors)
                        fromNode.Error ("Unknown struct type '" + varDecl.structTypeName + "' for '" + root + "'", fromNode);
                    return false;
                }
                memberStartIndex = 1;
                return true;
            }

            return false;
        }

        public void ValidateStructFieldAccess (IList<string> path, Parsed.Object fromNode)
        {
            StructDeclaration type;
            int start;
            if (!TryResolveStructPathContext (path, fromNode, out type, out start)) {
                if (path != null && path.Count >= 2 && path [0] != "base") {
                    var varDecl = ResolveVariableDeclaration (path [0], fromNode);
                    if (varDecl != null && varDecl.structTypeName == null)
                        fromNode.Error ("Cannot access '" + path [1] + "' on '" + path [0] + "' because it is not a struct-typed variable", fromNode);
                }
                return;
            }

            if (start >= path.Count) {
                // Bare type / self / Type.static with no field — ok as instance reference elsewhere
                return;
            }

            var typeCursor = type;
            for (int i = start; i < path.Count; i++) {
                var field = typeCursor.FindField (path [i]);
                if (field == null) {
                    fromNode.Error ("Struct '" + typeCursor.name + "' has no field named '" + path [i] + "'", fromNode);
                    return;
                }
                if (i < path.Count - 1) {
                    if (string.IsNullOrEmpty (field.structTypeName)) {
                        fromNode.Error ("Cannot access members through '" + path [i] + "' because it is not a struct-typed field", fromNode);
                        return;
                    }
                    var next = ResolveStruct (field.structTypeName);
                    if (next == null) {
                        fromNode.Error ("Unknown struct type '" + field.structTypeName + "' on field '" + path [i] + "'", fromNode);
                        return;
                    }
                    typeCursor = next;
                }
            }
        }

        public void ValidateStructMethodCall (IList<string> pathIncludingMethod, Parsed.Object fromNode)
        {
            if (pathIncludingMethod == null || pathIncludingMethod.Count < 2)
                return;

            string methodName = pathIncludingMethod [pathIncludingMethod.Count - 1];

            if (pathIncludingMethod [0] == "base") {
                // Validated separately in FunctionCall
                return;
            }

            StructDeclaration type;
            int start;
            if (!TryResolveStructPathContext (pathIncludingMethod, fromNode, out type, out start))
                return;

            // Intermediate fields before the method name
            int methodIndex = pathIncludingMethod.Count - 1;
            if (start > methodIndex) {
                fromNode.Error ("Missing method name in struct call", fromNode);
                return;
            }

            var typeCursor = type;
            for (int i = start; i < methodIndex; i++) {
                var field = typeCursor.FindField (pathIncludingMethod [i]);
                if (field == null) {
                    fromNode.Error ("Struct '" + typeCursor.name + "' has no field named '" + pathIncludingMethod [i] + "'", fromNode);
                    return;
                }
                if (string.IsNullOrEmpty (field.structTypeName)) {
                    fromNode.Error ("Cannot call methods through '" + pathIncludingMethod [i] + "' because it is not a struct-typed field", fromNode);
                    return;
                }
                var next = ResolveStruct (field.structTypeName);
                if (next == null) {
                    fromNode.Error ("Unknown struct type '" + field.structTypeName + "' on field '" + pathIncludingMethod [i] + "'", fromNode);
                    return;
                }
                typeCursor = next;
            }

            if (!typeCursor.HasMethod (methodName)) {
                fromNode.Error ("Struct '" + typeCursor.name + "' has no method named '" + methodName + "'", fromNode);
            }
        }

        public void DontFlattenContainer (Runtime.Container container)
        {
            _dontFlattenContainers.Add (container);
        }



        void NameConflictError (Parsed.Object obj, string name, Parsed.Object existingObj, string typeNameToPrint)
        {
            obj.Error (typeNameToPrint+" '" + name + "': name has already been used for a " + existingObj.typeName.ToLower() + " on " +existingObj.debugMetadata);
        }

        public static bool IsReservedKeyword (string name)
        {
            switch (name) {
            case "true":
            case "false":
            case "not":
            case "return":
            case "else":
            case "VAR":
            case "CONST":
            case "temp":
            case "LIST":
            case "struct":
            case "function":
            case "REFVAR":
            case "self":
            case "base":
            case "static":
            case "none":
            case "is":
            case "isnt":
                return true;
            }

            return false;
        }

        public enum SymbolType : uint
        {
        	Knot,
            Struct,
        	List,
        	ListItem,
        	Var,
        	SubFlowAndWeave,
        	Arg,
            Temp
        }

        // Check given symbol type against everything that's of a higher priority in the ordered SymbolType enum (above).
        // When the given symbol type level is reached, we early-out / return.
        public void CheckForNamingCollisions (Parsed.Object obj, Identifier identifier, SymbolType symbolType, string typeNameOverride = null)
        {
            string typeNameToPrint = typeNameOverride ?? obj.typeName;
            // Allow implicit 'self' parameter on struct methods
            if (identifier?.name == "self" && symbolType == SymbolType.Arg)
                return;

            if (IsReservedKeyword (identifier?.name)) {
                obj.Error ("'"+identifier?.name + "' cannot be used for the name of a " + typeNameToPrint.ToLower() + " because it's a reserved keyword");
                return;
            }

            if (FunctionCall.IsBuiltIn (identifier?.name)) {
                obj.Error ("'"+identifier?.name + "' cannot be used for the name of a " + typeNameToPrint.ToLower() + " because it's a built in function");
                return;
            }

            // Top level knots
            FlowBase knotOrFunction = ContentWithNameAtLevel (identifier?.name, FlowLevel.Knot) as FlowBase;
            if (knotOrFunction && (knotOrFunction != obj || symbolType == SymbolType.Arg)) {
                // Struct methods may share names with top-level functions: bare Foo() is
                // the global, self.Foo() is the method (Python-style qualification).
                var asFlow = obj as FlowBase;
                bool isStructMethod = asFlow != null && asFlow.parent is StructDeclaration;
                if (!isStructMethod) {
                    NameConflictError (obj, identifier?.name, knotOrFunction, typeNameToPrint);
                    return;
                }
            }

            // Structs — allow VAR Instance: StructType when names match (default proposal pattern)
            if (structs != null) {
                StructDeclaration existingStruct;
                if (structs.TryGetValue (identifier?.name, out existingStruct) && existingStruct != obj) {
                    var varAss = obj as VariableAssignment;
                    bool isMatchingStructInstance = varAss != null && varAss.structTypeName == existingStruct.name;
                    if (!isMatchingStructInstance) {
                        NameConflictError (obj, identifier?.name, existingStruct, typeNameToPrint);
                        return;
                    }
                }
            }

            if (symbolType < SymbolType.List) return;

            // Lists
            foreach (var namedListDef in _listDefs) {
                var listDefName = namedListDef.Key;
                var listDef = namedListDef.Value;
                if (identifier?.name == listDefName && obj != listDef && listDef.variableAssignment != obj) {
                    NameConflictError (obj, identifier?.name, listDef, typeNameToPrint);
                }

                // We don't check for conflicts between individual elements in
                // different lists because they are namespaced.
                if (!(obj is ListElementDefinition)) {
                    foreach (var item in listDef.itemDefinitions) {
                        if (identifier?.name == item.name) {
                            NameConflictError (obj, identifier?.name, item, typeNameToPrint);
                        }
                    }
                }
            }

            // Don't check for VAR->VAR conflicts because that's handled separately
            // (necessary since checking looks up in a dictionary)
            if (symbolType <= SymbolType.Var) return;

            // Global variable collision
            VariableAssignment varDecl = null;
            if (variableDeclarations.TryGetValue(identifier?.name, out varDecl) ) {
                if (varDecl != obj && varDecl.isGlobalDeclaration && varDecl.listDefinition == null) {
                    NameConflictError (obj, identifier?.name, varDecl, typeNameToPrint);
                }
            }

            if (symbolType < SymbolType.SubFlowAndWeave) return;

            // Stitches, Choices and Gathers
            var path = new Path (identifier);
            var targetContent = path.ResolveFromContext (obj);
            if (targetContent && targetContent != obj) {
                NameConflictError (obj, identifier?.name, targetContent, typeNameToPrint);
                return;
            }

            if (symbolType < SymbolType.Arg) return;

            // Arguments to the current flow
            if (symbolType != SymbolType.Arg) {
				FlowBase flow = obj as FlowBase;
				if( flow == null ) flow = obj.ClosestFlowBase ();
				if (flow && flow.hasParameters) {
					foreach (var arg in flow.arguments) {
						if (arg.identifier?.name == identifier?.name) {
							obj.Error (typeNameToPrint+" '" + name + "': Name has already been used for a argument to "+flow.identifier+" on " +flow.debugMetadata);
							return;
						}
					}
				}
            }
        }

        ErrorHandler _errorHandler;
        bool _hadError;
        bool _hadWarning;

        HashSet<Runtime.Container> _dontFlattenContainers = new HashSet<Runtime.Container>();

        Dictionary<string, Parsed.ListDefinition> _listDefs;
	}
}

