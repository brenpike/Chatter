using System;

namespace Samples.SharedKernel
{
    public class BusinessRuleException : Exception
    {
        public BusinessRuleException(string message) : this(message, null)
        { }

        public BusinessRuleException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
