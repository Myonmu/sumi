namespace Ink.Runtime
{
    /// <summary>
    /// REFVAR payload: global variable name of the target, or null for none.
    /// </summary>
    public class StructRefValue : Value<string>
    {
        public override ValueType valueType { get { return ValueType.StructRef; } }
        public override bool isTruthy { get { return !string.IsNullOrEmpty (value); } }

        public string targetName { get { return value; } }

        public StructRefValue (string targetName) : base (targetName)
        {
        }

        public StructRefValue () : this (null)
        {
        }

        public override Value Cast (ValueType newType)
        {
            if (newType == valueType)
                return this;
            throw BadCastException (newType);
        }

        public override Object Copy ()
        {
            return new StructRefValue (value);
        }

        public override string ToString ()
        {
            return value == null ? "none" : ("ref(" + value + ")");
        }
    }
}
