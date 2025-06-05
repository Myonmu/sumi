using System;
using System.Collections.Generic;
namespace Ink
{
    public partial class BaseParser
    {
        protected class InfixOperator
        {
            public string type;
            public int precedence;
            public bool requireWhitespace;

            public InfixOperator(string type, int precedence, bool requireWhitespace) {
                this.type = type;
                this.precedence = precedence;
                this.requireWhitespace = requireWhitespace;
            }

            public override string ToString ()
            {
                return type;
            }
        }
        
        protected InfixOperator ParseInfixOperator()
        {
            foreach (var op in _binaryOperators) {

                int ruleId = BeginRule ();

                if (ParseString (op.type) != null) {

                    if (op.requireWhitespace) {
                        if (Whitespace () == null) {
                            FailRule (ruleId);
                            continue;
                        }
                    }

                    return (InfixOperator) SucceedRule(ruleId, op);
                }

                FailRule (ruleId);
            }

            return null;
        }
        
        protected void RegisterBinaryOperator(string op, int precedence, bool requireWhitespace = false)
        {
            if (_binaryOperators == null)
            {
                _binaryOperators = new List<InfixOperator>();
                _maxBinaryOpLength = 0;
            }
            _binaryOperators.Add(new InfixOperator (op, precedence, requireWhitespace));
            _maxBinaryOpLength = Math.Max (_maxBinaryOpLength, op.Length);
        }
        
        List<InfixOperator> _binaryOperators;
        int _maxBinaryOpLength;
    }
}