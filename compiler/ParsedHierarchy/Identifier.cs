namespace Ink.Parsed {
    public class Identifier {
        public string name;
        public Expression dynamicNameExpression;
        public Runtime.DebugMetadata debugMetadata;

        public bool isDynamic {
            get { return dynamicNameExpression != null; }
        }

        public override string ToString()
        {
            if (isDynamic)
                return "{" + dynamicNameExpression + "}";
            return name;
        }

        public static Identifier Done = new Identifier { name = "DONE", debugMetadata = null };
    }
}
