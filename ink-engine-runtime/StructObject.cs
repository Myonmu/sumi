using System.Collections.Generic;

namespace Ink.Runtime
{
    /// <summary>
    /// A live struct instance: concrete type plus name-keyed field storage.
    /// </summary>
    public class StructObject
    {
        public string typeName { get; private set; }
        public Dictionary<string, Runtime.Object> storage { get; private set; }

        public StructObject (string typeName, Dictionary<string, Runtime.Object> storage = null)
        {
            this.typeName = typeName;
            this.storage = storage ?? new Dictionary<string, Runtime.Object> ();
        }

        public Runtime.Object GetField (string fieldName)
        {
            Runtime.Object value;
            if (storage.TryGetValue (fieldName, out value))
                return value;
            return null;
        }

        public void SetField (string fieldName, Runtime.Object value)
        {
            storage [fieldName] = value;
        }

        public StructObject DeepCopy ()
        {
            var copiedStorage = new Dictionary<string, Runtime.Object> ();
            foreach (var kv in storage) {
                copiedStorage [kv.Key] = DeepCopyValue (kv.Value);
            }
            return new StructObject (typeName, copiedStorage);
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

            // Scalars / pointers: Copy() is sufficient (REFVAR identity is StructRefValue above)
            return value.Copy ();
        }

        /// <summary>
        /// Create an instance seeded from a type descriptor's field defaults.
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
            return new StructObject (typeDesc.name, storage);
        }
    }
}
