namespace Ink.Runtime
{
    /// <summary>
    /// Pop a StructValue (or pointer to one), push the named field value.
    /// Follows REFVAR to the target instance when needed.
    /// </summary>
    public class StructFieldGet : Runtime.Object
    {
        public string fieldName { get; set; }

        public StructFieldGet (string fieldName)
        {
            this.fieldName = fieldName;
        }

        public StructFieldGet () {}

        public override string ToString ()
        {
            return "StructFieldGet(" + fieldName + ")";
        }
    }

    /// <summary>
    /// Stack (bottom→top): instance-or-pointer, value.
    /// Sets the named field on the instance (deep-copy for embedded structs; rebind for REFVAR).
    /// </summary>
    public class StructFieldSet : Runtime.Object
    {
        public string fieldName { get; set; }

        public StructFieldSet (string fieldName)
        {
            this.fieldName = fieldName;
        }

        public StructFieldSet () {}

        public override string ToString ()
        {
            return "StructFieldSet(" + fieldName + ")";
        }
    }

    /// <summary>
    /// Virtual (or base) method call.
    /// Stack (bottom→top): receiver pointer, arg0, arg1, ... argN
    /// Pops args+receiver, pushes them back for the function entry convention, then diverts.
    /// </summary>
    public class StructMethodCall : Runtime.Object
    {
        public string methodName { get; set; }
        public int argumentCount { get; set; }
        public bool isBaseCall { get; set; }
        /// <summary>For base calls: compile-time path. For virtual: unused (vtable lookup).</summary>
        public string targetPathString { get; set; }

        public StructMethodCall (string methodName, int argumentCount, bool isBaseCall = false, string targetPathString = null)
        {
            this.methodName = methodName;
            this.argumentCount = argumentCount;
            this.isBaseCall = isBaseCall;
            this.targetPathString = targetPathString;
        }

        public StructMethodCall () {}

        public override string ToString ()
        {
            return (isBaseCall ? "BaseCall(" : "StructMethodCall(") + methodName + "," + argumentCount + ")";
        }
    }

    /// <summary>
    /// Push a deep copy of the default instance for a struct type (by type name on stack as StringValue),
    /// or create from type name stored on this instruction.
    /// </summary>
    public class StructCreateDefault : Runtime.Object
    {
        public string typeName { get; set; }

        public StructCreateDefault (string typeName)
        {
            this.typeName = typeName;
        }

        public StructCreateDefault () {}

        public override string ToString ()
        {
            return "StructCreateDefault(" + typeName + ")";
        }
    }
}
