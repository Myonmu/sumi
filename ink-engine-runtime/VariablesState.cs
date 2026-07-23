using System;
using System.Collections.Generic;
using System.Linq;

namespace Ink.Runtime
{
    /// <summary>
    /// Encompasses all the global variables in an ink Story, and
    /// allows binding of a VariableChanged event so that that game
    /// code can be notified whenever the global variables change.
    /// </summary>
	public class VariablesState : IEnumerable<string>
    {
        public delegate void VariableChanged(string variableName, Runtime.Object newValue);
        public event VariableChanged variableChangedEvent;

        public StatePatch patch;

        public void StartVariableObservation()
        {
            _batchObservingVariableChanges = true;
            _changedVariablesForBatchObs = new HashSet<string> ();
        }

        public Dictionary<string, Object> CompleteVariableObservation()
        {
            _batchObservingVariableChanges = false;

            var changedVars = new Dictionary<string, Object> ();
            if (_changedVariablesForBatchObs != null) {
                foreach (var variableName in _changedVariablesForBatchObs) {
                    var currentValue = _globalVariables [variableName];
                    changedVars[variableName] = currentValue;
                }
            }

            // Patch may still be active - e.g. if we were in the middle of a background save
            if( patch != null ) {
                foreach(var variableName in patch.changedVariables) {
                    if( patch.TryGetGlobal(variableName, out Object patchedVal) ) {
                        changedVars[variableName] = patchedVal;
                    }
                }
            }

            _changedVariablesForBatchObs = null;
            return changedVars;
        }

        public void NotifyObservers(Dictionary<string, Object> changedVars)
        {
            foreach (var varToVal in changedVars) {
                variableChangedEvent (varToVal.Key, varToVal.Value);
            }
        }

        // Allow StoryState to change the current callstack, e.g. for
        // temporary function evaluation.
        public CallStack callStack {
            get {
                return _callStack;
            }
            set {
                _callStack = value;
            }
        }

        /// <summary>
        /// Get or set the value of a named global ink variable.
        /// The types available are the standard ink types. Certain
        /// types will be implicitly casted when setting.
        /// For example, doubles to floats, longs to ints, and bools
        /// to ints.
        /// <para>
        /// Dotted paths address struct fields on globals, e.g.
        /// <c>variablesState["Oswald.name"]</c> or nested
        /// <c>variablesState["party.scout.name"]</c> (REFVAR segments
        /// are followed automatically).
        /// </para>
        /// </summary>
        public object this[string variableName]
        {
            get {
                if (variableName != null && variableName.IndexOf ('.') >= 0)
                    return GetValueByDottedPath (variableName);

                Runtime.Object varContents;

                if (patch != null && patch.TryGetGlobal(variableName, out varContents))
                    return ValueObjectFollowingRefs (varContents);

                // Search main dictionary first.
                // If it's not found, it might be because the story content has changed,
                // and the original default value hasn't be instantiated.
                // Should really warn somehow, but it's difficult to see how...!
                if ( _globalVariables.TryGetValue (variableName, out varContents) || 
                     _defaultGlobalVariables.TryGetValue(variableName, out varContents) )
                    return ValueObjectFollowingRefs (varContents);
                else {
                    return null;
                }
            }
            set {
                if (variableName != null && variableName.IndexOf ('.') >= 0) {
                    SetValueByDottedPath (variableName, value);
                    return;
                }

                if (!_defaultGlobalVariables.ContainsKey (variableName))
                    throw new StoryException ("Cannot assign to a variable ("+variableName+") that hasn't been declared in the story");

                var existing = GetRawGlobalForPath (variableName);
                if (existing is StructRefValue) {
                    Runtime.Object stored;
                    if (value is StructRefValue)
                        stored = ((StructRefValue)value).Copy ();
                    else if (value == null)
                        stored = new StructRefValue (null);
                    else if (value is string)
                        stored = new StructRefValue ((string)value);
                    else
                        throw new StoryException ("REFVAR '" + variableName + "' must be set to a global name string, StructRefValue, or null");
                    SetGlobal (variableName, stored);
                    return;
                }
                
                var val = Runtime.Value.Create(value);
                if (val == null) {
                    if (value == null) {
                        throw new Exception ("Cannot pass null to VariableState");
                    } else {
                        throw new Exception ("Invalid value passed to VariableState: "+value.ToString());
                    }
                }

                SetGlobal (variableName, val);
            }
        }

        object ValueObjectFollowingRefs (Runtime.Object varContents)
        {
            var refVal = varContents as StructRefValue;
            if (refVal != null) {
                if (string.IsNullOrEmpty (refVal.targetName))
                    return null;
                var target = GetRawGlobalForPath (refVal.targetName);
                // Nested REFVAR globals
                while (target is StructRefValue nested) {
                    if (string.IsNullOrEmpty (nested.targetName))
                        return null;
                    target = GetRawGlobalForPath (nested.targetName);
                }
                var asValue = target as Value;
                return asValue != null ? asValue.valueObject : null;
            }
            var val = varContents as Runtime.Value;
            return val != null ? val.valueObject : null;
        }

        /// <summary>
        /// Get a global or a struct field via a dotted path (e.g. "Oswald.name").
        /// Returns null if the path cannot be resolved.
        /// </summary>
        public object GetValueByDottedPath (string path)
        {
            Runtime.Object runtimeVal;
            if (!TryGetRuntimeValueByDottedPath (path, out runtimeVal))
                return null;
            var asValue = runtimeVal as Value;
            return asValue != null ? asValue.valueObject : null;
        }

        /// <summary>
        /// Set a global struct field via a dotted path (e.g. "Oswald.name" = "Flinn").
        /// REFVAR fields accept a global name string, <see cref="StructRefValue"/>, or null for none.
        /// </summary>
        public void SetValueByDottedPath (string path, object value)
        {
            if (string.IsNullOrEmpty (path))
                throw new StoryException ("Cannot set empty variable path");

            var parts = path.Split ('.');
            if (parts.Length < 2)
                throw new StoryException ("Dotted path '" + path + "' must include at least one field (e.g. Oswald.name)");

            for (int p = 0; p < parts.Length; p++) {
                if (string.IsNullOrEmpty (parts [p]))
                    throw new StoryException ("Invalid dotted path '" + path + "'");
            }

            var rootName = parts [0];
            var current = ResolveStructRootForPath (rootName, path);
            if (current == null || current.value == null)
                throw new StoryException ("Cannot set '" + path + "': '" + rootName + "' is not a struct global");

            // Walk to the parent struct of the final field
            for (int i = 1; i < parts.Length - 1; i++) {
                current = ResolveStructChild (current, parts [i], path);
            }

            var fieldName = parts [parts.Length - 1];
            var existing = current.value.GetField (fieldName);

            if (value is DivertTargetValue && current.value.isDynamic) {
                current.value.RemoveField (fieldName);
                current.value.SetMethod (fieldName, (DivertTargetValue)((DivertTargetValue)value).Copy ());
                if (variableChangedEvent != null) {
                    var rootVal = GetRawGlobalForPath (rootName);
                    if (rootVal != null)
                        variableChangedEvent (rootName, rootVal);
                }
                return;
            }

            if (!current.value.isDynamic && !current.value.HasField (fieldName) && !(existing is StructRefValue)) {
                throw new StoryException ("Cannot add field '" + fieldName + "' on closed struct '" + current.value.typeName + "'");
            }

            Runtime.Object stored;
            if (existing is StructRefValue) {
                // Rebind REFVAR: string = global name, null = none
                if (value is StructRefValue)
                    stored = ((StructRefValue)value).Copy ();
                else if (value == null)
                    stored = new StructRefValue (null);
                else if (value is string)
                    stored = new StructRefValue ((string)value);
                else
                    throw new StoryException ("REFVAR field '" + path + "' must be set to a global name string, StructRefValue, or null");
            }
            else {
                if (value is StructObject)
                    stored = new StructValue ((StructObject)value).Copy ();
                else if (value is StructValue)
                    stored = ((StructValue)value).Copy ();
                else {
                    stored = Value.Create (value);
                    if (stored == null)
                        throw new Exception ("Invalid value passed to VariableState path '" + path + "': " + (value == null ? "null" : value.ToString ()));
                }
            }

            if (current.value.isDynamic)
                current.value.RemoveMethod (fieldName);
            current.value.SetField (fieldName, stored);

            // Notify observers interested in the root global
            if (variableChangedEvent != null) {
                var rootVal = GetRawGlobalForPath (rootName);
                if (rootVal != null)
                    variableChangedEvent (rootName, rootVal);
            }
        }

        StructValue ResolveStructRootForPath (string rootName, string fullPath)
        {
            var runtimeVal = GetRawGlobalForPath (rootName);
            while (runtimeVal is StructRefValue refVal) {
                if (string.IsNullOrEmpty (refVal.targetName))
                    throw new StoryException ("Cannot set '" + fullPath + "': REFVAR '" + rootName + "' is none");
                runtimeVal = GetRawGlobalForPath (refVal.targetName);
            }
            return runtimeVal as StructValue;
        }

        bool TryGetRuntimeValueByDottedPath (string path, out Runtime.Object runtimeVal)
        {
            runtimeVal = null;
            if (string.IsNullOrEmpty (path))
                return false;

            var parts = path.Split ('.');
            if (parts.Length == 0)
                return false;

            for (int p = 0; p < parts.Length; p++) {
                if (string.IsNullOrEmpty (parts [p]))
                    return false;
            }

            runtimeVal = GetRawGlobalForPath (parts [0]);
            if (runtimeVal == null)
                return false;

            // Follow REFVAR at the root (global REFVAR variables)
            while (runtimeVal is StructRefValue rootRef) {
                if (string.IsNullOrEmpty (rootRef.targetName)) {
                    runtimeVal = null;
                    return parts.Length == 1; // none is a valid terminal get → null value
                }
                runtimeVal = GetRawGlobalForPath (rootRef.targetName);
                if (runtimeVal == null)
                    return false;
            }

            for (int i = 1; i < parts.Length; i++) {
                var structVal = runtimeVal as StructValue;
                if (structVal == null || structVal.value == null) {
                    runtimeVal = null;
                    return false;
                }

                var field = structVal.value.GetField (parts [i]);
                if (field == null) {
                    runtimeVal = null;
                    return false;
                }

                var refVal = field as StructRefValue;
                if (refVal != null) {
                    if (string.IsNullOrEmpty (refVal.targetName)) {
                        runtimeVal = null;
                        return i == parts.Length - 1; // none is a valid terminal get → null value
                    }
                    runtimeVal = GetRawGlobalForPath (refVal.targetName);
                    if (runtimeVal == null)
                        return false;
                    // Nested REFVAR globals as targets
                    while (runtimeVal is StructRefValue nestedRef) {
                        if (string.IsNullOrEmpty (nestedRef.targetName)) {
                            runtimeVal = null;
                            return i == parts.Length - 1;
                        }
                        runtimeVal = GetRawGlobalForPath (nestedRef.targetName);
                        if (runtimeVal == null)
                            return false;
                    }
                    continue;
                }

                runtimeVal = field;
            }

            return true;
        }

        Runtime.Object GetRawGlobalForPath (string name)
        {
            Runtime.Object varContents = null;
            if (patch != null && patch.TryGetGlobal (name, out varContents))
                return varContents;
            if (_globalVariables.TryGetValue (name, out varContents))
                return varContents;
            if (_defaultGlobalVariables != null && _defaultGlobalVariables.TryGetValue (name, out varContents))
                return varContents;
            return null;
        }

        StructValue ResolveStructChild (StructValue parent, string fieldName, string fullPath)
        {
            var field = parent.value.GetField (fieldName);
            var refVal = field as StructRefValue;
            if (refVal != null) {
                if (string.IsNullOrEmpty (refVal.targetName))
                    throw new StoryException ("Cannot set '" + fullPath + "': REFVAR '" + fieldName + "' is none");
                var target = GetRawGlobalForPath (refVal.targetName) as StructValue;
                if (target == null || target.value == null)
                    throw new StoryException ("Cannot set '" + fullPath + "': REFVAR '" + fieldName + "' target '" + refVal.targetName + "' is not a struct");
                return target;
            }

            var nested = field as StructValue;
            if (nested == null || nested.value == null)
                throw new StoryException ("Cannot set '" + fullPath + "': '" + fieldName + "' is not a struct field");
            return nested;
        }

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

        /// <summary>
        /// Enumerator to allow iteration over all global variables by name.
        /// </summary>
		public IEnumerator<string> GetEnumerator()
		{
			return _globalVariables.Keys.GetEnumerator();
		}

        public VariablesState (CallStack callStack, ListDefinitionsOrigin listDefsOrigin)
        {
            _globalVariables = new Dictionary<string, Object> ();
            _callStack = callStack;
            _listDefsOrigin = listDefsOrigin;
        }

        public void ApplyPatch()
        {
            foreach(var namedVar in patch.globals) {
                _globalVariables[namedVar.Key] = namedVar.Value;
            }

            if(_changedVariablesForBatchObs != null ) {
                foreach (var name in patch.changedVariables)
                    _changedVariablesForBatchObs.Add(name);
            }

            patch = null;
        }

        /// <summary>
        /// Struct/dynamic instances are mutable and stored by reference. During
        /// newline lookahead (and background-save patching), field writes must
        /// not mutate the live globals or <see cref="Story.RestoreStateSnapshot"/>
        /// cannot roll them back — unlike plain ints, which replace the global
        /// entry via <see cref="SetGlobal"/>. Eagerly copy every struct global
        /// into the patch so subsequent in-place field sets touch the copy only.
        /// </summary>
        public void SnapshotStructGlobalsIntoPatch()
        {
            if (patch == null)
                return;

            // Snapshot keys first — patch.SetGlobal may grow the dictionary.
            var names = new List<string> (_globalVariables.Keys);
            foreach (var name in names) {
                Runtime.Object current;
                if (patch.TryGetGlobal (name, out current)) {
                    var patchedStruct = current as StructValue;
                    if (patchedStruct != null)
                        patch.SetGlobal (name, (StructValue)patchedStruct.Copy ());
                    continue;
                }

                Runtime.Object globalVal;
                if (!_globalVariables.TryGetValue (name, out globalVal))
                    continue;

                var structVal = globalVal as StructValue;
                if (structVal != null)
                    patch.SetGlobal (name, (StructValue)structVal.Copy ());
            }
        }

        public void SetJsonToken(Dictionary<string, object> jToken)
        {
            _globalVariables.Clear();

            foreach (var varVal in _defaultGlobalVariables) {
                object loadedToken;
                if( jToken.TryGetValue(varVal.Key, out loadedToken) ) {
                    _globalVariables[varVal.Key] = Json.JTokenToRuntimeObject(loadedToken);
                } else {
                    _globalVariables[varVal.Key] = varVal.Value;
                }
            }
        }

        /// <summary>
        /// When saving out JSON state, we can skip saving global values that
        /// remain equal to the initial values that were declared in ink.
        /// This makes the save file (potentially) much smaller assuming that
        /// at least a portion of the globals haven't changed. However, it
        /// can also take marginally longer to save in the case that the 
        /// majority HAVE changed, since it has to compare all globals.
        /// It may also be useful to turn this off for testing worst case
        /// save timing.
        /// </summary>
        public static bool dontSaveDefaultValues = true;

        public void WriteJson(SimpleJson.Writer writer)
        {
            writer.WriteObjectStart();
            foreach (var keyVal in _globalVariables)
            {
                var name = keyVal.Key;
                var val = keyVal.Value;

                if(dontSaveDefaultValues) {
                    // Don't write out values that are the same as the default global values
                    Runtime.Object defaultVal;
                    if (_defaultGlobalVariables != null && _defaultGlobalVariables.TryGetValue(name, out defaultVal))
                    {
                        if (RuntimeObjectsEqual(val, defaultVal))
                            continue;
                    }
                }


                writer.WritePropertyStart(name);
                Json.WriteRuntimeObject(writer, val);
                writer.WritePropertyEnd();
            }
            writer.WriteObjectEnd();
        }

        public bool RuntimeObjectsEqual(Runtime.Object obj1, Runtime.Object obj2)
        {
            if (obj1.GetType() != obj2.GetType()) return false;

            // Perform equality on int/float/bool manually to avoid boxing
            var boolVal = obj1 as BoolValue;
            if( boolVal != null ) {
                return boolVal.value == ((BoolValue)obj2).value;
            }

            var intVal = obj1 as IntValue;
            if( intVal != null ) {
                return intVal.value == ((IntValue)obj2).value;
            }

            var floatVal = obj1 as FloatValue;
            if (floatVal != null)
            {
                return floatVal.value == ((FloatValue)obj2).value;
            }

            // Other Value type (using proper Equals: list, string, divert path)
            var val1 = obj1 as Value;
            var val2 = obj2 as Value;
            if( val1 != null ) {
                return val1.valueObject.Equals(val2.valueObject);
            }

            throw new System.Exception("FastRoughDefinitelyEquals: Unsupported runtime object type: "+obj1.GetType());
        }

        public Runtime.Object GetVariableWithName(string name)
        {
            return GetVariableWithName (name, -1);
        }

        public Runtime.Object TryGetDefaultVariableValue (string name)
        {
            Runtime.Object val = null;
            _defaultGlobalVariables.TryGetValue (name, out val);
            return val;
        }

		public bool GlobalVariableExistsWithName(string name)
		{
			return _globalVariables.ContainsKey(name) || _defaultGlobalVariables != null && _defaultGlobalVariables.ContainsKey(name);
		}

        Runtime.Object GetVariableWithName(string name, int contextIndex)
        {
            Runtime.Object varValue = GetRawVariableWithName (name, contextIndex);

            // Get value from pointer?
            var varPointer = varValue as VariablePointerValue;
            if (varPointer) {
                varValue = ValueAtVariablePointer (varPointer);
            }

            // Follow global REFVAR to the target instance (none stays as StructRefValue)
            var refVal = varValue as StructRefValue;
            if (refVal != null && !string.IsNullOrEmpty (refVal.targetName)) {
                varValue = GetVariableWithName (refVal.targetName, 0);
            }

            return varValue;
        }

        Runtime.Object GetRawVariableWithName(string name, int contextIndex)
        {
            Runtime.Object varValue = null;

            // 0 context = global
            if (contextIndex == 0 || contextIndex == -1) {
                if (patch != null && patch.TryGetGlobal(name, out varValue))
                    return varValue;

                if ( _globalVariables.TryGetValue (name, out varValue) )
                    return varValue;

                // Getting variables can actually happen during globals set up since you can do
                //  VAR x = A_LIST_ITEM
                // So _defaultGlobalVariables may be null.
                // We need to do this check though in case a new global is added, so we need to
                // revert to the default globals dictionary since an initial value hasn't yet been set.
                if( _defaultGlobalVariables != null && _defaultGlobalVariables.TryGetValue(name, out varValue) ) {
                    return varValue;
                }

                var listItemValue = _listDefsOrigin.FindSingleItemListWithName (name);
                if (listItemValue)
                    return listItemValue;
            } 

            // Temporary
            varValue = _callStack.GetTemporaryVariableWithName (name, contextIndex);

            return varValue;
        }

        public Runtime.Object ValueAtVariablePointer(VariablePointerValue pointer)
        {
            return GetVariableWithName (pointer.variableName, pointer.contextIndex);
        }

        public void Assign(VariableAssignment varAss, Runtime.Object value)
        {
            var name = varAss.variableName;
            int contextIndex = -1;

            // Are we assigning to a global variable?
            bool setGlobal = false;
            if (varAss.isNewDeclaration) {
                setGlobal = varAss.isGlobal;
            } else {
                setGlobal = GlobalVariableExistsWithName (name);
            }

            // Constructing new variable pointer reference
            if (varAss.isNewDeclaration) {
                var varPointer = value as VariablePointerValue;
                if (varPointer) {
                    var fullyResolvedVariablePointer = ResolveVariablePointer (varPointer);
                    value = fullyResolvedVariablePointer;
                }

            } 

            // Assign to existing variable pointer?
            // Then assign to the variable that the pointer is pointing to by name.
            else {

                // De-reference variable reference to point to
                VariablePointerValue existingPointer = null;
                do {
                    existingPointer = GetRawVariableWithName (name, contextIndex) as VariablePointerValue;
                    if (existingPointer) {
                        name = existingPointer.variableName;
                        contextIndex = existingPointer.contextIndex;
                        setGlobal = (contextIndex == 0);
                    }
                } while(existingPointer);
            }

            // Struct values are deep-copied on assignment (value semantics)
            var structVal = value as StructValue;
            if (structVal != null)
                value = structVal.Copy ();

            // Rebinding a REFVAR global: keep StructRefValue; coerce pointer → ref
            if (setGlobal) {
                var existingRaw = GetRawGlobalForPath (name);
                if (existingRaw is StructRefValue) {
                    if (value is VariablePointerValue)
                        value = new StructRefValue (((VariablePointerValue)value).variableName);
                    else if (value is StructValue)
                        throw new StoryException ("Cannot assign a struct value to REFVAR '" + name + "'; assign a global name (or none)");
                }
            }

            if (setGlobal) {
                SetGlobal (name, value);
            } else {
                _callStack.SetTemporaryVariable (name, value, varAss.isNewDeclaration, contextIndex);
            }
        }

        public void SnapshotDefaultGlobals ()
        {
            _defaultGlobalVariables = new Dictionary<string, Object> (_globalVariables);
        }

        void RetainListOriginsForAssignment (Runtime.Object oldValue, Runtime.Object newValue)
        {
            var oldList = oldValue as ListValue;
            var newList = newValue as ListValue;
            if (oldList && newList && newList.value.Count == 0)
                newList.value.SetInitialOriginNames (oldList.value.originNames);
        }

        public void SetGlobal(string variableName, Runtime.Object value)
        {
            Runtime.Object oldValue = null;
            if( patch == null || !patch.TryGetGlobal(variableName, out oldValue) )
                _globalVariables.TryGetValue (variableName, out oldValue);

            ListValue.RetainListOriginsForAssignment (oldValue, value);

            if (patch != null)
                patch.SetGlobal(variableName, value);
            else
                _globalVariables [variableName] = value;

            if (variableChangedEvent != null && !value.Equals (oldValue)) {

                if (_batchObservingVariableChanges) {
                    if (patch != null)
                        patch.AddChangedVariable(variableName);
                    else if(_changedVariablesForBatchObs != null)
                        _changedVariablesForBatchObs.Add (variableName);
                } else {
                    variableChangedEvent (variableName, value);
                }
            }
        }

        // Given a variable pointer with just the name of the target known, resolve to a variable
        // pointer that more specifically points to the exact instance: whether it's global,
        // or the exact position of a temporary on the callstack.
        VariablePointerValue ResolveVariablePointer(VariablePointerValue varPointer)
        {
            int contextIndex = varPointer.contextIndex;

            if( contextIndex == -1 )
                contextIndex = GetContextIndexOfVariableNamed (varPointer.variableName);

            var valueOfVariablePointedTo = GetRawVariableWithName (varPointer.variableName, contextIndex);

            // Extra layer of indirection:
            // When accessing a pointer to a pointer (e.g. when calling nested or 
            // recursive functions that take a variable references, ensure we don't create
            // a chain of indirection by just returning the final target.
            var doubleRedirectionPointer = valueOfVariablePointedTo as VariablePointerValue;
            if (doubleRedirectionPointer) {
                return doubleRedirectionPointer;
            } 

            // Make copy of the variable pointer so we're not using the value direct from
            // the runtime. Temporary must be local to the current scope.
            else {
                return new VariablePointerValue (varPointer.variableName, contextIndex);
            }
        }

        // 0  if named variable is global
        // 1+ if named variable is a temporary in a particular call stack element
        int GetContextIndexOfVariableNamed(string varName)
        {
            if (GlobalVariableExistsWithName(varName))
                return 0;

            return _callStack.currentElementIndex;
        }

        Dictionary<string, Runtime.Object> _globalVariables;

        Dictionary<string, Runtime.Object> _defaultGlobalVariables;

        // Used for accessing temporary variables
        CallStack _callStack;
        HashSet<string> _changedVariablesForBatchObs;
        ListDefinitionsOrigin _listDefsOrigin;
        bool _batchObservingVariableChanges;
    }
}

