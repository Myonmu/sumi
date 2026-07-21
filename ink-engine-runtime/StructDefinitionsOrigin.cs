using System.Collections.Generic;

namespace Ink.Runtime
{
    public class StructDefinitionsOrigin
    {
        public List<StructDeclaration> structs {
            get {
                var list = new List<StructDeclaration> ();
                foreach (var kv in _structs)
                    list.Add (kv.Value);
                return list;
            }
        }

        public StructDefinitionsOrigin (List<StructDeclaration> structs)
        {
            _structs = new Dictionary<string, StructDeclaration> ();
            if (structs == null)
                return;
            foreach (var s in structs)
                _structs [s.name] = s;
        }

        public bool TryGetDefinition (string name, out StructDeclaration def)
        {
            return _structs.TryGetValue (name, out def);
        }

        public StructDeclaration GetDefinition (string name)
        {
            StructDeclaration def;
            if (_structs.TryGetValue (name, out def))
                return def;
            return null;
        }

        Dictionary<string, StructDeclaration> _structs;
    }
}
