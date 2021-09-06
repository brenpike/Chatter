using Moq;
using System;

namespace Chatter.Testing.Core.Creators.Common
{
    public class TypeCreator : Creator<Type>
    {
        private readonly Mock<Type> _typeMock = new Mock<Type>();

        public TypeCreator(INewContext newContext, Type type = null)
            : base(newContext, type)
        {
            Creation = _typeMock.Object;
        }

        public TypeCreator WithNamespace(string @namespace)
        {
            _typeMock.SetupGet(a => a.Namespace).Returns(@namespace);
            return this;
        }
    }
}
