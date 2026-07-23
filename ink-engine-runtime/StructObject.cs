using System.Collections.Generic;

namespace Ink.Runtime
{
    public enum StructKind
    {
        Struct,
        Dynamic
    }

    /// <summary>
    /// A live struct/dynamic instance: concrete type, kind, field storage, and (for dynamics) instance method slots.
    /// </summary>
    public class StructObject
    {
        public string typeName { get; private set; }
        public StructKind kind { get; private set; }
        public bool isDynamic { get { return kind == StructKind.Dynamic; } }
        public Dictionary<string, Runtime.Object> storage { get; private set; }
        /// <summary>Instance method slots (dynamic only): name → DivertTargetValue.</summary>
        public Dictionary<string, Runtime.Object> methods { get; private set; }

        public StructObject (string typeName, Dictionary<string, Runtime.Object> storage = null, StructKind kind = StructKind.Struct, Dictionary<string, Runtime.Object> methods = null)
        {
            this.typeName = typeName;
            this.kind = kind;
            this.storage = storage ?? new Dictionary<string, Runtime.Object> ();
            this.methods = methods ?? new Dictionary<string, Runtime.Object> ();
        }

        public Runtime.Object GetField (string fieldName)
        {
            Runtime.Object value;
            if (storage.TryGetValue (fieldName, out value))
                return value;
            return null;
        }

        public bool HasField (string fieldName)
        {
            return storage.ContainsKey (fieldName);
        }

        public void SetField (string fieldName, Runtime.Object value)
        {
            storage [fieldName] = value;
        }

        public bool RemoveField (string fieldName)
        {
            return storage.Remove (fieldName);
        }

        public DivertTargetValue GetMethod (string methodName)
        {
            Runtime.Object value;
            if (methods != null && methods.TryGetValue (methodName, out value))
                return value as DivertTargetValue;
            return null;
        }

        public bool HasMethod (string methodName)
        {
            return methods != null && methods.ContainsKey (methodName);
        }

        public void SetMethod (string methodName, DivertTargetValue target)
        {
            if (methods == null)
                methods = new Dictionary<string, Runtime.Object> ();
            methods [methodName] = target;
        }

        public bool RemoveMethod (string methodName)
        {
            return methods != null && methods.Remove (methodName);
        }

        public bool HasSlot (string slotName)
        {
            return HasField (slotName) || HasMethod (slotName);
        }

        public bool RemoveSlot (string slotName)
        {
            bool removed = RemoveField (slotName);
            removed |= RemoveMethod (slotName);
            return removed;
        }

        public StructObject DeepCopy ()
        {
            var copiedStorage = new Dictionary<string, Runtime.Object> ();
            foreach (var kv in storage) {
                copiedStorage [kv.Key] = DeepCopyValue (kv.Value);
            }
            Dictionary<string, Runtime.Object> copiedMethods = null;
            if (methods != null && methods.Count > 0) {
                copiedMethods = new Dictionary<string, Runtime.Object> ();
                foreach (var kv in methods) {
                    copiedMethods [kv.Key] = DeepCopyValue (kv.Value);
                }
            }
            return new StructObject (typeName, copiedStorage, kind, copiedMethods);
        }

        public static Runtime.Object DeepCopyValue (Runtime.Object value)
        {
            if (value == null)
                return null;

            var structVal = value as StructValue;
            if (structVal != null)
                return new StructValue (structVal.value.DeepCopy ());

            var refVal = value as StructRefValue;
            if (refVal != null)
                return new StructRefValue (refVal.targetName);

            var listVal = value as ListValue;
            if (listVal != null)
                return new ListValue (listVal.value);

            // Scalars / pointers / divert targets: Copy() is sufficient
            return value.Copy ();
        }

        /// <summary>
        /// Create an empty anonymous dynamic instance (VAR x: dynamic).
        /// </summary>
        public static StructObject CreateEmptyDynamic (string typeName)
        {
            return new StructObject (typeName, null, StructKind.Dynamic, null);
        }

        /// <summary>
        /// Create an instance seeded from a type descriptor's field defaults (and method slots for dynamics).
        /// </summary>
        public static StructObject CreateFromDefaults (StructDeclaration typeDesc)
        {
            var storage = new Dictionary<string, Runtime.Object> ();
            foreach (var field in typeDesc.fields) {
                if (field.kind == StructFieldKind.RefVar) {
                    storage [field.name] = DeepCopyValue (field.defaultValue) ?? new StructRefValue (null);
                } else if (field.typeName != null && field.defaultValue is StructValue) {
                    storage [field.name] = DeepCopyValue (field.defaultValue);
                } else if (field.defaultValue != null) {
                    storage [field.name] = DeepCopyValue (field.defaultValue);
                } else {
                    storage [field.name] = new IntValue (0);
                }
            }

            Dictionary<string, Runtime.Object> instanceMethods = null;
            if (typeDesc.kind == StructKind.Dynamic && typeDesc.methods != null) {
                instanceMethods = new Dictionary<string, Runtime.Object> ();
                foreach (var kv in typeDesc.methods) {
                    instanceMethods [kv.Key] = new DivertTargetValue (new Path (kv.Value));
                }
            }

            return new StructObject (typeDesc.name, storage, typeDesc.kind, instanceMethods);
        }
    }
}
