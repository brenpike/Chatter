using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenGettingImplementedInterfacesThatMatchOpenGenericType : Testing.Core.Context
    {
        [Fact]
        public void MustReturnImplementedInterfacesThatAreGenericAndHaveMatchingGenericTypeDef()
        {
            var genericTypeDef = typeof(IMessageHandler<>);
            var interfaceType = New.Common().Type
                .AsGeneric()
                .WithGenericTypeDef(genericTypeDef);
            var type = New.Common().Type
                .WithImplementedInterfaces(interfaceType).Creation;

            var result = TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(type, genericTypeDef);
            Assert.Contains(interfaceType, result);
        }

        [Fact]
        public void MustNotReturnImplementedInterfacesThatAreNotGeneric()
        {
            var genericTypeDef = typeof(IMessageHandler<>);
            var interfaceType = New.Common().Type;
            var type = New.Common().Type
                .WithImplementedInterfaces(interfaceType).Creation;

            var result = TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(type, genericTypeDef);
            Assert.DoesNotContain(interfaceType, result);
            Assert.Empty(result);
        }

        [Fact]
        public void MustNotReturnImplementedInterfacesWithGenericTypeDefThatDoesNotMatch()
        {
            var genericTypeDef = typeof(IMessageHandler<>);
            var interfaceType = New.Common().Type
                .AsGeneric()
                .WithGenericTypeDef(genericTypeDef);
            var type = New.Common().Type
                .WithImplementedInterfaces(interfaceType).Creation;

            var result = TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(type, typeof(IEnumerable<>));
            Assert.DoesNotContain(interfaceType, result);
            Assert.Empty(result);
        }

        [Fact]
        public void MustReturnEmptyListIfTypeHasNotImplementedInterfaces()
        {
            var type = New.Common().Type;

            var result = TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(type, typeof(IEnumerable<>));
            Assert.Empty(result);
        }

        [Fact]
        public void MustReturnEmptyListIfOpenGenericTypeToMatchIsNull()
        {
            var type = New.Common().Type;

            var result = TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(type, null);
            Assert.Empty(result);
        }

        [Fact]
        public void MustThrowIfTypeIsNull()
            => Assert.Throws<ArgumentNullException>(() => TypeExtensions.GetImplementedInterfacesThatMatchOpenGenericType(null, typeof(IEnumerable<>)));
    }
}
