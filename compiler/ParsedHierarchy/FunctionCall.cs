using System.Collections.Generic;

namespace Ink.Parsed
{
    public class FunctionCall : Expression
    {
        public string name { get { return _proxyDivert.target.firstComponent; } }
        public Divert proxyDivert { get { return _proxyDivert; } }
        public List<Expression> arguments { get { return _proxyDivert.arguments; } }
        public Runtime.Divert runtimeDivert { get { return _proxyDivert.runtimeDivert; } }
        public bool isChoiceCount { get { return name == "CHOICE_COUNT"; } }
        public bool isTurns { get { return name == "TURNS"; } }
        public bool isTurnsSince { get { return name == "TURNS_SINCE"; } }
        public bool isRandom { get { return name == "RANDOM"; } }
        public bool isSeedRandom { get { return name == "SEED_RANDOM"; } }
        public bool isListRange { get { return name == "LIST_RANGE"; } }
        public bool isListRandom { get { return name == "LIST_RANDOM"; } }
        public bool isReadCount { get { return name == "READ_COUNT"; } }

        public bool shouldPopReturnedValue;

        public FunctionCall (List<Identifier> functionCallIdentifiers, List<Expression> arguments)
        {
            _proxyDivert = new Parsed.Divert(new Path(functionCallIdentifiers), arguments);
            _proxyDivert.isFunctionCall = true;
            AddContent (_proxyDivert);
        }

        public override void GenerateIntoContainer (Runtime.Container container)
        {
            var foundList = story.ResolveList (name);

            bool usingProxyDivert = false;

            if (isChoiceCount) {

                if (arguments.Count > 0)
                    Error ("The CHOICE_COUNT() function shouldn't take any arguments");

                container.AddContent (Runtime.ControlCommand.ChoiceCount ());

            } else if (isTurns) {

                if (arguments.Count > 0)
                    Error ("The TURNS() function shouldn't take any arguments");

                container.AddContent (Runtime.ControlCommand.Turns ());

            } else if (isTurnsSince || isReadCount) {

                var divertTarget = arguments [0] as DivertTarget;
                var variableDivertTarget = arguments [0] as VariableReference;

                if (arguments.Count != 1 || (divertTarget == null && variableDivertTarget == null)) {
                    Error ("The " + name + "() function should take one argument: a divert target to the target knot, stitch, gather or choice you want to check. e.g. TURNS_SINCE(-> myKnot)");
                    return;
                }

                if (divertTarget) {
                    _divertTargetToCount = divertTarget;
                    AddContent (_divertTargetToCount);

                    _divertTargetToCount.GenerateIntoContainer (container);
                } else {
                    _variableReferenceToCount = variableDivertTarget;
                    AddContent (_variableReferenceToCount);

                    _variableReferenceToCount.GenerateIntoContainer (container);
                }

                if (isTurnsSince)
                    container.AddContent (Runtime.ControlCommand.TurnsSince ());
                else
                    container.AddContent (Runtime.ControlCommand.ReadCount ());

            } else if (isRandom) {
                if (arguments.Count != 2)
                    Error ("RANDOM should take 2 parameters: a minimum and a maximum integer");

                // We can type check single values, but not complex expressions
                for (int arg = 0; arg < arguments.Count; arg++) {
                    if (arguments [arg] is Number) {
                        var num = arguments [arg] as Number;
                        if (!(num.value is int)) {
                            string paramName = arg == 0 ? "minimum" : "maximum";
                            Error ("RANDOM's " + paramName + " parameter should be an integer");
                        }
                    }

                    arguments [arg].GenerateIntoContainer (container);
                }

                container.AddContent (Runtime.ControlCommand.Random ());

            } else if (isSeedRandom) {
                if (arguments.Count != 1)
                    Error ("SEED_RANDOM should take 1 parameter - an integer seed");

                var num = arguments [0] as Number;
                if (num && !(num.value is int)) {
                    Error ("SEED_RANDOM's parameter should be an integer seed");
                }

                arguments [0].GenerateIntoContainer (container);

                container.AddContent (Runtime.ControlCommand.SeedRandom ());

            } else if (isListRange) {
                if (arguments.Count != 3)
                    Error ("LIST_RANGE should take 3 parameters - a list, a min and a max");

                for (int arg = 0; arg < arguments.Count; arg++)
                    arguments [arg].GenerateIntoContainer (container);

                container.AddContent (Runtime.ControlCommand.ListRange ());

            } else if( isListRandom ) {
                if (arguments.Count != 1)
                    Error ("LIST_RANDOM should take 1 parameter - a list");

                arguments [0].GenerateIntoContainer (container);

                container.AddContent (Runtime.ControlCommand.ListRandom ());

            } else if (Runtime.NativeFunctionCall.CallExistsWithName (name)) {

                var nativeCall = Runtime.NativeFunctionCall.CallWithName (name);

                if (nativeCall.numberOfParameters != arguments.Count) {
                    var msg = name + " should take " + nativeCall.numberOfParameters + " parameter";
                    if (nativeCall.numberOfParameters > 1)
                        msg += "s";
                    Error (msg);
                }

                for (int arg = 0; arg < arguments.Count; arg++)
                    arguments [arg].GenerateIntoContainer (container);

                container.AddContent (Runtime.NativeFunctionCall.CallWithName (name));
            } else if (foundList != null) {
                if (arguments.Count > 1)
                    Error ("Can currently only construct a list from one integer (or an empty list from a given list definition)");

                // List item from given int
                if (arguments.Count == 1) {
                    container.AddContent (new Runtime.StringValue (name));
                    arguments [0].GenerateIntoContainer (container);
                    container.AddContent (Runtime.ControlCommand.ListFromInt ());
                }

                // Empty list with given origin.
                else {
                    var list = new Runtime.InkList ();
                    list.SetInitialOriginName (name);
                    container.AddContent (new Runtime.ListValue (list));
                }
            }

            // Struct method call: recv.Method(...) or base.Method(...)
            else if (TryGenerateStructMethodCall (container)) {
                usingProxyDivert = false;
            }

            // Normal function call
            else {
                container.AddContent (_proxyDivert.runtimeObject);
                usingProxyDivert = true;
            }

            // Don't attempt to resolve as a divert if we're not doing a normal function call
            if( !usingProxyDivert ) content.Remove (_proxyDivert);

            // Function calls that are used alone on a tilda-based line:
            //  ~ func()
            // Should tidy up any returned value from the evaluation stack,
            // since it's unused.
            if (shouldPopReturnedValue)
                container.AddContent (Runtime.ControlCommand.PopEvaluatedValue ());
        }

        bool TryGenerateStructMethodCall (Runtime.Container container)
        {
            var comps = _proxyDivert.target?.components;
            if (comps == null || comps.Count < 2)
                return false;

            var pathNames = StructPathCodegen.PathNames (comps);

            if (!IsStructMethodCallPath (story, pathNames, this))
                return false;

            var methodId = comps [comps.Count - 1];
            string methodName = methodId.isDynamic ? null : methodId.name;
            bool isBase = comps [0].name == "base";

            // Push receiver as pointer
            if (isBase) {
                container.AddContent (new Runtime.VariablePointerValue ("self"));
            } else {
                // Receiver path without method name
                string root = comps [0].name;
                if (root == "self") {
                    container.AddContent (new Runtime.VariablePointerValue ("self"));
                } else {
                    container.AddContent (new Runtime.VariablePointerValue (root));
                }
                // Intermediate fields: party.scout.Method
                for (int i = 1; i < comps.Count - 1; i++) {
                    if (comps [i].name == "static" && !comps [i].isDynamic)
                        continue;
                    StructPathCodegen.GenerateFieldGet (container, comps [i]);
                }
            }

            if (methodId.isDynamic)
                StructPathCodegen.GenerateNameOntoStack (container, methodId);

            // Explicit arguments (self is implicit)
            if (arguments != null) {
                foreach (var arg in arguments)
                    arg.GenerateIntoContainer (container);
            }

            int argc = arguments != null ? arguments.Count : 0;
            string basePath = null;
            if (isBase) {
                if (methodName == null) {
                    Error ("base.{...}() is not supported; base calls need a literal method name");
                    return true;
                }
                var method = ClosestStructMethod ();
                var structDecl = method?.parent as StructDeclaration;
                if (structDecl != null && structDecl.baseCallPaths != null)
                    structDecl.baseCallPaths.TryGetValue (methodName, out basePath);
            }

            container.AddContent (new Runtime.StructMethodCall (methodName, argc, isBase, basePath));
            return true;
        }

        /// <summary>
        /// True when recv.Method(...) should use struct virtual call rather than a divert.
        /// </summary>
        public static bool IsStructMethodCallPath (Story story, List<string> pathNames, Parsed.Object fromNode)
        {
            if (story == null || pathNames == null || pathNames.Count < 2)
                return false;

            if (pathNames [0] == "base" || pathNames [0] == "self")
                return true;

            StructDeclaration recvType;
            int start;
            if (story.TryResolveStructPathContext (pathNames, fromNode, out recvType, out start, reportErrors: false))
                return true;

            // Don't steal knot.stitch() calls
            if (story.ContentWithNameAtLevel (pathNames [0], FlowLevel.Knot) != null)
                return false;

            // Variable may be registered; treat dotted calls on variables as struct methods
            // when the story defines structs/dynamics, or the variable is typed as dynamic/struct.
            if (story.ResolveVariableWithName (pathNames [0], fromNode).found) {
                if (story.structs != null && story.structs.Count > 0)
                    return true;
                var typeName = story.ResolveStructTypeNameForName (pathNames [0], fromNode);
                if (typeName != null)
                    return true;
            }

            return false;
        }

        Stitch ClosestStructMethod ()
        {
            return Story.ClosestStructMethod (this);
        }

        public override void ResolveReferences (Story context)
        {
            // LIST constructors (Mood() / Mood(2)) are not function diverts.
            // Struct field defaults never run GenerateIntoContainer, so skip divert resolve.
            if (context.ResolveList (name) != null) {
                if (arguments != null) {
                    foreach (var arg in arguments)
                        arg.ResolveReferences (context);
                }
                return;
            }

            var comps = _proxyDivert.target?.components;
            if (comps != null && comps.Count >= 2) {
                var pathNames = StructPathCodegen.PathNames (comps);

                if (IsStructMethodCallPath (context, pathNames, this)) {
                    if (arguments != null) {
                        foreach (var arg in arguments)
                            arg.ResolveReferences (context);
                    }

                    if (pathNames [0] == "base") {
                        var method = ClosestStructMethod ();
                        var structDecl = method?.parent as StructDeclaration;
                        string methodName = pathNames [pathNames.Count - 1];
                        if (methodName == null) {
                            Error ("base.{...}() is not supported; base calls need a literal method name");
                        } else if (structDecl == null || structDecl.baseCallPaths == null || !structDecl.baseCallPaths.ContainsKey (methodName)) {
                            Error ("base." + methodName + "() is only valid inside an overriding method");
                        }
                    } else {
                        context.ValidateStructMethodCall (pathNames, this);
                    }
                    return;
                }
            }

            base.ResolveReferences (context);

            // If we aren't using the proxy divert after all (e.g. if
            // it's a native function call), but we still have arguments,
            // we need to make sure they get resolved since the proxy divert
            // is no longer in the content array.
            if (!content.Contains(_proxyDivert) && arguments != null) {
                foreach (var arg in arguments)
                    arg.ResolveReferences (context);
            }

            if( _divertTargetToCount ) {
                var divert = _divertTargetToCount.divert;
                var attemptingTurnCountOfVariableTarget = divert.runtimeDivert.variableDivertName != null;

                if( attemptingTurnCountOfVariableTarget ) {
                    Error("When getting the TURNS_SINCE() of a variable target, remove the '->' - i.e. it should just be TURNS_SINCE("+divert.runtimeDivert.variableDivertName+")");
                    return;
                }

                var targetObject = divert.targetContent;
                if( targetObject == null ) {
                    if( !attemptingTurnCountOfVariableTarget ) {
                        Error("Failed to find target for TURNS_SINCE: '"+divert.target+"'");
                    }
                } else {
                    targetObject.containerForCounting.turnIndexShouldBeCounted = true;
                }
            }

            else if( _variableReferenceToCount ) {
                var runtimeVarRef = _variableReferenceToCount.runtimeVarRef;
                if( runtimeVarRef.pathForCount != null ) {
                    Error("Should be "+name+"(-> "+_variableReferenceToCount.name+"). Usage without the '->' only makes sense for variable targets.");
                }
            }
        }

        public static bool IsBuiltIn(string name)
        {
            if (Runtime.NativeFunctionCall.CallExistsWithName (name))
                return true;

            return name == "CHOICE_COUNT"
                || name == "TURNS_SINCE"
                || name == "TURNS"
                || name == "RANDOM"
                || name == "SEED_RANDOM"
                || name == "LIST_VALUE"
                || name == "LIST_RANDOM"
                || name == "READ_COUNT";
        }

        public override string ToString ()
        {
            var strArgs = string.Join (", ", arguments.ToStringsArray());
            return string.Format ("{0}({1})", name, strArgs);
        }

        Parsed.Divert _proxyDivert;
        Parsed.DivertTarget _divertTargetToCount;
        Parsed.VariableReference _variableReferenceToCount;
    }
}

