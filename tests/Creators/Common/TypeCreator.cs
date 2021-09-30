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

        public TypeCreator WithFullName(string fullName)
        {
            _typeMock.SetupGet(a => a.FullName).Returns(fullName);
            return this;
        }

        public TypeCreator AsGeneric()
        {
            _typeMock.SetupGet(t => t.IsGenericType).Returns(true);
            return this;
        }

        public TypeCreator AsGenericTypeParameter()
        {
            _typeMock.SetupGet(t => t.IsGenericParameter).Returns(true);
            return this;
        }

        public TypeCreator WithGenericArguments(params Type[] genericArguments)
        {
            _typeMock.Setup(t => t.GetGenericArguments()).Returns(genericArguments);
            _typeMock.SetupGet(t => t.GenericTypeArguments).Returns(genericArguments);
            _typeMock.SetupGet(t => t.IsGenericTypeDefinition).Returns(false);
            _typeMock.SetupGet(t => t.IsGenericType).Returns(true);
            return this;
        }

        public TypeCreator WithImplementedInterfaces(params Type[] implementedInterfaces)
        {
            _typeMock.Setup(t => t.GetInterfaces()).Returns(implementedInterfaces);
            return this;
        }

        public TypeCreator WithGenericTypeDef(Type genericTypeDef)
        {
            _typeMock.Setup(t => t.GetGenericTypeDefinition()).Returns(genericTypeDef);
            return this;
        }
    }
}
