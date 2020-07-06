using System;
using System.Reflection;

namespace Chatter.MessageBrokers.Exceptions
{
    public class BrokeredMessageAttributeNotFoundException : Exception
    {
        public BrokeredMessageAttributeNotFoundException(MemberInfo typeWithoutBrokeredMessageAttribute)
            : base($"'{typeWithoutBrokeredMessageAttribute.Name}' is not decorated with {nameof(BrokeredMessageAttribute)}.")
        {
            this.Source = nameof(typeWithoutBrokeredMessageAttribute);
        }
    }
}
