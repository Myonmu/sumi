using System.Collections.Generic;
using System.Linq;

namespace Ink.Parsed
{
	public class Divert : Parsed.Object
	{
		public Parsed.Path target { get; protected set; }
        public Parsed.Object targetContent { get; protected set; }
        public List<Expression> arguments { get; protected set; }
		public Runtime.Divert runtimeDivert { get; protected set; }
        public bool isFunctionCall { get; set; }
        public bool isEmpty { get; set; }
        public bool isTunnel { get; set; }
        public bool isThread { get; set; }
        public bool isEnd {
            get {
                return target != null && target.dotSeparatedComponents == "END";
            }
        }
        public bool isDone {
            get {
                return target != null && target.dotSeparatedComponents == "DONE";
            }
        }

        Runtime.StructStitchDivert _structStitchDivert;
        List<string> _structStitchPathNames;

        public Divert (Parsed.Path target, List<Expression> arguments = null)
		{
			this.target = target;
            this.arguments = arguments;

            if (arguments != null) {
                AddContent (arguments.Cast<Parsed.Object> ().ToList ());
            }
            if (target != null)
                StructPathCodegen.AddDynamicNameContent (this, target.components);
		}

        public Divert (Parsed.Object targetContent)
        {
            this.targetContent = targetContent;
        }

		public override Runtime.Object GenerateRuntimeObject ()
		{
            // End = end flow immediately
            // Done = return from thread or instruct the flow that it's safe to exit
            if (isEnd) {
                return Runtime.ControlCommand.End ();
            }
            if (isDone) {
                return Runtime.ControlCommand.Done ();
            }

            Runtime.Object structStitchRuntime;
            if (TryGenerateStructStitchDivert (out structStitchRuntime))
                return structStitchRuntime;

            runtimeDivert = new Runtime.Divert ();

            // Path with evaluated components: -> knot.{name}
            if (target != null && StructPathCodegen.HasDynamicComponent (target.components)) {
                return GenerateDivertFromDynamicPath ();
            }

            // Normally we resolve the target content during the
            // Resolve phase, since we expect all runtime objects to
            // be available in order to find the final runtime path for
            // the destination. However, we need to resolve the target
            // (albeit without the runtime target) early so that
            // we can get information about the arguments - whether
            // they're by reference - since it affects the code we
            // generate here.
            ResolveTargetContent ();

            // Bare sibling stitch name inside a struct (e.g. -> wave) resolves to a
            // Stitch under StructDeclaration; still needs self + virtual divert.
            if (TryGenerateStructStitchDivertFromResolvedTarget (out structStitchRuntime))
                return structStitchRuntime;

            CheckArgumentValidity ();

            // Passing arguments to the knot
            bool requiresArgCodeGen = arguments != null && arguments.Count > 0;
            if ( requiresArgCodeGen || isFunctionCall || isTunnel || isThread ) {

                var container = new Runtime.Container ();

                // Generate code for argument evaluation
                // This argument generation is coded defensively - it should
                // attempt to generate the code for all the parameters, even if
                // they don't match the expected arguments. This is so that the
                // parameter objects themselves are generated correctly and don't
                // get into a state of attempting to resolve references etc
                // without being generated.
                if (requiresArgCodeGen) {

                    // Function calls already in an evaluation context
                    if (!isFunctionCall) {
                        container.AddContent (Runtime.ControlCommand.EvalStart());
                    }

                    List<FlowBase.Argument> targetArguments = null;
                    if( targetContent )
                        targetArguments = (targetContent as FlowBase).arguments;

                    for (var i = 0; i < arguments.Count; ++i) {
                        Expression argToPass = arguments [i];
                        FlowBase.Argument argExpected = null;
                        if( targetArguments != null && i < targetArguments.Count )
                            argExpected = targetArguments [i];

                        // Pass by reference: argument needs to be a variable reference
                        if (argExpected != null && argExpected.isByReference) {
                            GenerateByRefArgument (container, argToPass, argExpected);
                        }

                        // Normal value being passed: evaluate it as normal
                        else {
                            argToPass.GenerateIntoContainer (container);
                        }
                    }

                    // Function calls were already in an evaluation context
                    if (!isFunctionCall) {
                        container.AddContent (Runtime.ControlCommand.EvalEnd());
                    }
                }


                // Starting a thread? A bit like a push to the call stack below... but not.
                // It sort of puts the call stack on a thread stack (argh!) - forks the full flow.
                if (isThread) {
                    container.AddContent(Runtime.ControlCommand.StartThread());
                }

                // If this divert is a function call, tunnel, we push to the call stack
                // so we can return again
                else if (isFunctionCall || isTunnel) {
                    runtimeDivert.pushesToStack = true;
                    runtimeDivert.stackPushType = isFunctionCall ? Runtime.PushPopType.Function : Runtime.PushPopType.Tunnel;
                }

                // Jump into the "function" (knot/stitch)
                container.AddContent (runtimeDivert);

                return container;
            }

            // Simple divert
            else {
                return runtimeDivert;
            }
		}

        void GenerateByRefArgument (Runtime.Container container, Expression argToPass, FlowBase.Argument argExpected)
        {
            var varRef = argToPass as VariableReference;
            if (varRef == null) {
                Error ("Expected variable name to pass by reference to 'ref " + argExpected.identifier + "' but saw " + argToPass.ToString ());
                return;
            }

            // Check that we're not attempting to pass a read count by reference
            var targetPath = new Path(varRef.pathIdentifiers);
            Parsed.Object targetForCount = targetPath.ResolveFromContext (this);
            if (targetForCount != null) {
                Error ("can't pass a read count by reference. '" + targetPath.dotSeparatedComponents+"' is a knot/stitch/label, but '"+target.dotSeparatedComponents+"' requires the name of a VAR to be passed.");
                return;
            }

            var varPointer = new Runtime.VariablePointerValue (varRef.name);
            container.AddContent (varPointer);
        }

        bool TryGenerateStructStitchDivert (out Runtime.Object result)
        {
            result = null;
            if (target == null || isThread || isFunctionCall)
                return false;

            var comps = target.components;
            if (comps == null || comps.Count < 2)
                return false;

            var pathNames = StructPathCodegen.PathNames (comps);

            var storyContext = story;
            if (storyContext == null)
                return false;

            // Diverting to a function method is invalid as flow — but storing -> Type.static.Method
            // as a DivertTargetValue (function pointer for dynamic method slots) is allowed.
            if (storyContext.IsStructMethodDivertPath (pathNames, this)) {
                if (this.parent is DivertTarget)
                    return false;
                var methodLabel = pathNames [pathNames.Count - 1] ?? "{...}";
                Error ("Method '" + methodLabel + "' can't be diverted to. It can only be called as a function");
                result = Runtime.ControlCommand.Done ();
                return true;
            }

            if (!storyContext.IsStructStitchDivertPath (pathNames, this))
                return false;

            if (this.parent is DivertTarget) {
                Error ("can't store a struct stitch divert target in a variable");
                return false;
            }

            var stitchId = comps [comps.Count - 1];
            string stitchName = stitchId.isDynamic ? null : stitchId.name;
            bool isBase = comps [0].name == "base";

            List<FlowBase.Argument> authorArgs = null;
            if (!isBase && stitchName != null) {
                StructDeclaration recvType;
                int start;
                if (storyContext.TryResolveStructPathContext (pathNames, this, out recvType, out start, reportErrors: false)) {
                    var typeCursor = recvType;
                    for (int i = start; i < pathNames.Count - 1; i++) {
                        if (pathNames [i] == null)
                            break;
                        var field = typeCursor.FindField (pathNames [i]);
                        if (field == null || string.IsNullOrEmpty (field.structTypeName))
                            break;
                        typeCursor = storyContext.ResolveStruct (field.structTypeName);
                        if (typeCursor == null)
                            break;
                    }
                    authorArgs = typeCursor?.FindStitch (stitchName)?.authorArguments;
                }
            } else if (isBase && stitchName != null) {
                var enclosing = Story.ClosestStructStitch (this);
                var structDecl = enclosing?.parent as StructDeclaration;
                authorArgs = structDecl?.FindStitch (stitchName)?.authorArguments;
            }

            return EmitStructStitchDivert (comps, pathNames, stitchName, isBase, authorArgs, out result);
        }

        bool TryGenerateStructStitchDivertFromResolvedTarget (out Runtime.Object result)
        {
            result = null;
            if (isThread || isFunctionCall)
                return false;

            var stitch = targetContent as Stitch;
            if (stitch == null || stitch.isFunction || !(stitch.parent is StructDeclaration))
                return false;

            if (this.parent is DivertTarget) {
                Error ("can't store a struct stitch divert target in a variable");
                return false;
            }

            // Diverting to a function method by bare name
            // (shouldn't happen for isFunction stitches — filtered above)

            var pathNames = new List<string> { "self", stitch.name };
            return EmitStructStitchDivert (
                new List<Identifier> {
                    new Identifier { name = "self" },
                    new Identifier { name = stitch.name }
                },
                pathNames, stitchName: stitch.name, isBase: false,
                authorArgs: StructDeclaration.AuthorFacingArguments (stitch), out result);
        }

        bool EmitStructStitchDivert (List<Identifier> comps, List<string> pathNames, string stitchName, bool isBase, List<FlowBase.Argument> authorArgs, out Runtime.Object result)
        {
            result = null;
            var container = new Runtime.Container ();

            // Receiver + args must be pushed inside expression evaluation (same as knot divert args).
            container.AddContent (Runtime.ControlCommand.EvalStart ());

            if (isBase) {
                container.AddContent (new Runtime.VariablePointerValue ("self"));
            } else {
                string root = pathNames [0];
                if (root == "self") {
                    container.AddContent (new Runtime.VariablePointerValue ("self"));
                } else {
                    container.AddContent (new Runtime.VariablePointerValue (root));
                }
                for (int i = 1; i < comps.Count - 1; i++) {
                    if (comps [i].name == "static" && !comps [i].isDynamic)
                        continue;
                    StructPathCodegen.GenerateFieldGet (container, comps [i]);
                }
            }

            var stitchId = comps [comps.Count - 1];
            if (stitchId.isDynamic)
                StructPathCodegen.GenerateNameOntoStack (container, stitchId);

            if (arguments != null) {
                for (var i = 0; i < arguments.Count; ++i) {
                    Expression argToPass = arguments [i];
                    FlowBase.Argument argExpected = null;
                    if (authorArgs != null && i < authorArgs.Count)
                        argExpected = authorArgs [i];

                    if (argExpected != null && argExpected.isByReference)
                        GenerateByRefArgument (container, argToPass, argExpected);
                    else
                        argToPass.GenerateIntoContainer (container);
                }
            }

            container.AddContent (Runtime.ControlCommand.EvalEnd ());

            int argc = arguments != null ? arguments.Count : 0;
            string basePath = null;
            if (isBase) {
                if (stitchName == null) {
                    Error ("-> base.{...} is not supported; base stitch diverts need a literal name");
                    result = Runtime.ControlCommand.Done ();
                    return true;
                }
                var enclosing = Story.ClosestStructStitch (this);
                var structDecl = enclosing?.parent as StructDeclaration;
                if (structDecl != null && structDecl.stitchBaseCallPaths != null)
                    structDecl.stitchBaseCallPaths.TryGetValue (stitchName, out basePath);
            }

            _structStitchDivert = new Runtime.StructStitchDivert (stitchName, argc, isBase, isTunnel, basePath);
            _structStitchPathNames = pathNames;
            container.AddContent (_structStitchDivert);
            result = container;
            return true;
        }

        Runtime.Object GenerateDivertFromDynamicPath ()
        {
            var container = new Runtime.Container ();

            bool requiresArgCodeGen = arguments != null && arguments.Count > 0;
            if (requiresArgCodeGen || isFunctionCall || isTunnel || isThread) {
                if (requiresArgCodeGen && !isFunctionCall)
                    container.AddContent (Runtime.ControlCommand.EvalStart ());

                if (requiresArgCodeGen) {
                    for (var i = 0; i < arguments.Count; ++i)
                        arguments [i].GenerateIntoContainer (container);
                }

                if (requiresArgCodeGen && !isFunctionCall)
                    container.AddContent (Runtime.ControlCommand.EvalEnd ());

                if (isThread)
                    container.AddContent (Runtime.ControlCommand.StartThread ());
                else if (isFunctionCall || isTunnel) {
                    runtimeDivert.pushesToStack = true;
                    runtimeDivert.stackPushType = isFunctionCall ? Runtime.PushPopType.Function : Runtime.PushPopType.Tunnel;
                }
            }

            // Build path string then divert
            container.AddContent (Runtime.ControlCommand.EvalStart ());
            StructPathCodegen.GeneratePathStringOntoStack (container, target.components);
            container.AddContent (Runtime.ControlCommand.EvalEnd ());

            runtimeDivert.pathFromStack = true;
            container.AddContent (runtimeDivert);
            return container;
        }

        // When the divert is to a target that's actually a variable name
        // rather than an explicit knot/stitch name, try interpretting it
        // as such by getting the variable name.
        public string PathAsVariableName()
        {
            return target.firstComponent;
        }


        void ResolveTargetContent()
        {
            if (isEmpty || isEnd) {
                return;
            }

            if (targetContent == null) {

                // Is target of this divert a variable name that will be de-referenced
                // at runtime? If so, there won't be any further reference resolution
                // we can do at this point.
                var variableTargetName = PathAsVariableName ();
                if (variableTargetName != null) {
                    var flowBaseScope = ClosestFlowBase ();
                    var resolveResult = flowBaseScope.ResolveVariableWithName (variableTargetName, fromNode: this);
                    if (resolveResult.found) {

                        // Make sure that the flow was typed correctly, given that we know that this
                        // is meant to be a divert target
                        if (resolveResult.isArgument) {
                            var argument = resolveResult.ownerFlow.arguments.Where (a => a.identifier.name == variableTargetName).First();
                            if ( !argument.isDivertTarget ) {
                                Error ("Since '" + argument.identifier + "' is used as a variable divert target (on "+this.debugMetadata+"), it should be marked as: -> " + argument.identifier, resolveResult.ownerFlow);
                            }
                        }

                        runtimeDivert.variableDivertName = variableTargetName;
                        return;

                    }
                }

                targetContent = target.ResolveFromContext (this);

                // Type.static.Method — struct methods are named content, not flow-path children
                if (targetContent == null && target != null && target.numberOfComponents >= 3
                    && target.components [1]?.name == "static") {
                    var typeName = target.components [0]?.name;
                    var methodName = target.components [target.numberOfComponents - 1]?.name;
                    var structDecl = story?.ResolveStruct (typeName);
                    if (structDecl != null && methodName != null) {
                        string methodPath;
                        if (structDecl.flattenedMethods != null
                            && structDecl.flattenedMethods.TryGetValue (methodName, out methodPath)) {
                            var declaringTypeName = methodPath.Split ('.') [0];
                            var declaring = story.ResolveStruct (declaringTypeName) ?? structDecl;
                            if (declaring.ownMethods != null) {
                                foreach (var m in declaring.ownMethods) {
                                    if (m.name == methodName) {
                                        targetContent = m;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public override void ResolveReferences(Story context)
		{
            if (isEmpty || isEnd || isDone) {
                return;
            }

            // Late detection: GenerateRuntimeObject may have run before structs were registered
            if (_structStitchDivert == null && target != null && target.components != null && target.components.Count >= 2
                && !StructPathCodegen.HasDynamicComponent (target.components)) {
                var pathNames = StructPathCodegen.PathNames (target.components);
                if (context.IsStructStitchDivertPath (pathNames, this)) {
                    // Should not happen if story was available during codegen
                    Error ("Internal error: struct stitch divert was not generated for '" + target.dotSeparatedComponents + "'");
                }
            }

            if (_structStitchDivert != null) {
                if (arguments != null) {
                    foreach (var arg in arguments)
                        arg.ResolveReferences (context);
                }
                int argc = arguments != null ? arguments.Count : 0;
                context.ValidateStructStitchDivert (_structStitchPathNames, this, argc);
                return;
            }

            if (runtimeDivert != null && runtimeDivert.pathFromStack) {
                base.ResolveReferences (context);
                return;
            }

            if (targetContent) {
                runtimeDivert.targetPath = targetContent.runtimePath;
            }

            // Resolve children (the arguments)
            base.ResolveReferences (context);

            // May be null if it's a built in function (e.g. TURNS_SINCE)
            // or if it's a variable target.
            var targetFlow = targetContent as FlowBase;
            if (targetFlow) {
                if (!targetFlow.isFunction && this.isFunctionCall) {
                    base.Error (targetFlow.identifier + " hasn't been marked as a function, but it's being called as one. Do you need to delcare the knot as '== function " + targetFlow.identifier + " =='?");
                } else if (targetFlow.isFunction && !this.isFunctionCall && !(this.parent is DivertTarget)) {
                    base.Error (targetFlow.identifier + " can't be diverted to. It can only be called as a function since it's been marked as such: '" + targetFlow.identifier + "(...)'");
                }

                // Struct-typed parameters: check simple variable / type-name arguments
                if (isFunctionCall && arguments != null && targetFlow.arguments != null) {
                    int n = System.Math.Min (arguments.Count, targetFlow.arguments.Count);
                    for (int i = 0; i < n; ++i) {
                        var flowArg = targetFlow.arguments [i];
                        if (flowArg.structTypeName == null)
                            continue;
                        var varRef = arguments [i] as VariableReference;
                        if (varRef == null || varRef.path == null || varRef.path.Count != 1)
                            continue;
                        string actualType = context.ResolveStructTypeNameForName (varRef.path [0], this);
                        if (actualType == null && context.ResolveStruct (varRef.path [0]) != null)
                            actualType = varRef.path [0];
                        if (actualType != null && !context.StructTypeIsCompatible (actualType, flowArg.structTypeName)) {
                            Error ("Parameter '" + flowArg.identifier + "' expects struct type '" + flowArg.structTypeName + "' but got '" + actualType + "'", arguments [i]);
                        }
                    }
                }
            }

            // Check validity of target content
            bool targetWasFound = targetContent != null;
            bool isBuiltIn = false;
            bool isExternal = false;

            if (target.numberOfComponents == 1 ) {

                // BuiltIn means TURNS_SINCE, CHOICE_COUNT, RANDOM or SEED_RANDOM
                isBuiltIn = FunctionCall.IsBuiltIn (target.firstComponent);

                // Client-bound function?
                isExternal = context.IsExternal (target.firstComponent);

                if (isBuiltIn || isExternal) {
                    if (!isFunctionCall) {
                        base.Error (target.firstComponent + " must be called as a function: ~ " + target.firstComponent + "()");
                    }
                    if (isExternal) {
                        runtimeDivert.isExternal = true;
                        if( arguments != null )
                            runtimeDivert.externalArgs = arguments.Count;
                        runtimeDivert.pushesToStack = false;
                        runtimeDivert.targetPath = new Runtime.Path (this.target.firstComponent);
                        CheckExternalArgumentValidity (context);
                    }
                    return;
                }
            }

            // Variable target?
            if (runtimeDivert.variableDivertName != null) {
                return;
            }

            if( !targetWasFound && !isBuiltIn && !isExternal )
                Error ("target not found: '" + target + "'");
		}

        // Returns false if there's an error
        void CheckArgumentValidity()
        {
            if (isEmpty)
                return;

            // Argument passing: Check for errors in number of arguments
            var numArgs = 0;
            if (arguments != null && arguments.Count > 0)
                numArgs = arguments.Count;

            // Missing content?
            // Can't check arguments properly. It'll be due to some
            // other error though, so although there's a problem and
            // we report false, we don't need to report a specific error.
            // It may also be because it's a valid call to an external
            // function, that we check at the resolve stage.
            if (targetContent == null) {
                return;
            }

            FlowBase targetFlow = targetContent as FlowBase;

            // No error, crikey!
            if (numArgs == 0 && (targetFlow == null || !targetFlow.hasParameters)) {
                return;
            }

            if (targetFlow == null && numArgs > 0) {
                Error ("target needs to be a knot or stitch in order to pass arguments");
                return;
            }

            if (targetFlow.arguments == null && numArgs > 0) {
                Error ("target (" + targetFlow.name + ") doesn't take parameters");
                return;
            }

            if( this.parent is DivertTarget ) {
                if (numArgs > 0)
                    Error ("can't store arguments in a divert target variable");
                return;
            }

            var paramCount = targetFlow.arguments.Count;
            if (paramCount != numArgs) {

                string butClause;
                if (numArgs == 0) {
                    butClause = "but there weren't any passed to it";
                } else if (numArgs < paramCount) {
                    butClause = "but only got " + numArgs;
                } else {
                    butClause = "but got " + numArgs;
                }
                Error ("to '" + targetFlow.identifier + "' requires " + paramCount + " arguments, "+butClause);
                return;
            }

            // Light type-checking for divert target arguments
            for (int i = 0; i < paramCount; ++i) {
                FlowBase.Argument flowArg = targetFlow.arguments [i];
                Parsed.Expression divArgExpr = arguments [i];

                // Expecting a divert target as an argument, let's do some basic type checking
                if (flowArg.isDivertTarget) {

                    // Not passing a divert target or any kind of variable reference?
                    var varRef = divArgExpr as VariableReference;
                    if (!(divArgExpr is DivertTarget) && varRef == null ) {
                        Error ("Target '" + targetFlow.identifier + "' expects a divert target for the parameter named -> " + flowArg.identifier + " but saw " + divArgExpr, divArgExpr);
                    }

                    // Passing 'a' instead of '-> a'?
                    // i.e. read count instead of divert target
                    else if (varRef != null) {

                        // Unfortunately have to manually resolve here since we're still in code gen
                        var knotCountPath = new Path(varRef.pathIdentifiers);
                        Parsed.Object targetForCount = knotCountPath.ResolveFromContext (varRef);
                        if (targetForCount != null) {
                            Error ("Passing read count of '" + knotCountPath.dotSeparatedComponents + "' instead of a divert target. You probably meant '" + knotCountPath + "'");
                        }
                    }
                }
            }

            if (targetFlow == null) {
                Error ("Can't call as a function or with arguments unless it's a knot or stitch");
                return;
            }

            return;
        }

        void CheckExternalArgumentValidity(Story context)
        {
            string externalName = target.firstComponent;
            ExternalDeclaration external = null;
            var found = context.externals.TryGetValue(externalName, out external);
            System.Diagnostics.Debug.Assert (found, "external not found");

            int externalArgCount = external.argumentNames.Count;
            int ownArgCount = 0;
            if (arguments != null) {
                ownArgCount = arguments.Count;
            }

            if (ownArgCount != externalArgCount) {
                Error ("incorrect number of arguments sent to external function '" + externalName + "'. Expected " + externalArgCount + " but got " + ownArgCount);
            }
        }

        public override void Error (string message, Object source = null, bool isWarning = false)
        {
            // Could be getting an error from a nested Divert
            if (source != this && source) {
                base.Error (message, source);
                return;
            }

            if (isFunctionCall) {
                base.Error ("Function call " + message, source, isWarning);
            } else {
                base.Error ("Divert " + message, source, isWarning);
            }
        }

        public override string ToString ()
        {
            if (target != null)
                return target.ToString ();
            else
                return "-> <empty divert>";
        }

	}
}
