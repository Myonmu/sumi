using System.Collections.Generic;

namespace Ink.Runtime
{
    public enum StructFieldKind
    {
        Var,
        RefVar
    }

    public class StructFieldSlot
    {
        public string name;
        public StructFieldKind kind;
        /// <summary>Struct type name if this field is struct-typed; otherwise null.</summary>
        public string typeName;
        public Runtime.Object defaultValue;

        public StructFieldSlot (string name, StructFieldKind kind, string typeName = null, Runtime.Object defaultValue = null)
        {
            this.name = name;
            this.kind = kind;
            this.typeName = typeName;
            this.defaultValue = defaultValue;
        }
    }

    /// <summary>
    /// Compile-time / story JSON type descriptor for a struct.
    /// </summary>
    public class StructDeclaration
    {
        public string name { get; private set; }
        public List<string> bases { get; private set; }
        public List<StructFieldSlot> fields { get; private set; }
        /// <summary>Flattened method name → divert path string (e.g. Character.static.ReactFurious).</summary>
        public Dictionary<string, string> methods { get; private set; }
        /// <summary>Override method → inherited implementation path for base calls.</summary>
        public Dictionary<string, string> baseCalls { get; private set; }

        Dictionary<string, StructFieldSlot> _fieldsByName;

        public StructDeclaration (
            string name,
            List<string> bases,
            List<StructFieldSlot> fields,
            Dictionary<string, string> methods,
            Dictionary<string, string> baseCalls = null)
        {
            this.name = name;
            this.bases = bases ?? new List<string> ();
            this.fields = fields ?? new List<StructFieldSlot> ();
            this.methods = methods ?? new Dictionary<string, string> ();
            this.baseCalls = baseCalls ?? new Dictionary<string, string> ();

            _fieldsByName = new Dictionary<string, StructFieldSlot> ();
            foreach (var field in this.fields)
                _fieldsByName [field.name] = field;
        }

        public bool TryGetField (string fieldName, out StructFieldSlot slot)
        {
            return _fieldsByName.TryGetValue (fieldName, out slot);
        }

        public bool HasField (string fieldName)
        {
            return _fieldsByName.ContainsKey (fieldName);
        }

        public bool TryGetMethodPath (string methodName, out string path)
        {
            return methods.TryGetValue (methodName, out path);
        }

        public bool TryGetBaseCallPath (string methodName, out string path)
        {
            return baseCalls.TryGetValue (methodName, out path);
        }
    }
}
