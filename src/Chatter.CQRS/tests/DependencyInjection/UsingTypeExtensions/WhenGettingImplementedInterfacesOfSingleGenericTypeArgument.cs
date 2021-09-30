using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenGettingImplementedInterfacesOfSingleGenericTypeArgument : Testing.Core.Context
    {
        [Fact]
        public void MustGetImplementedInterfacesIfTypeHasExactlyOneGenericArgument()
        {
            var implementedInterfaceType = typeof(ICommand);
            var genericArgumentType = New.Common().Type
                .WithImplementedInterfaces(implementedInterfaceType).Creation;
            var type = New.Common().Type
                .WithGenericArguments(genericArgumentType)
                .Creation;

            var result = TypeExtensions.GetImplementedInterfacesOfSingleGenericTypeArgument(type);
            Assert.Contains(implementedInterfaceType, result);
        }

        [Fact]
        public void MustReturnEMptyIfTypeHasNoGenericArguments()
        {
            var type = New.Common().Type.Creation;

            var result = TypeExtensions.GetImplementedInterfacesOfSingleGenericTypeArgument(type);
            Assert.Empty(result);
        }

        [Fact]
        public void MustThrowIfTypeHasMoreThanOneGenericArgument()
        {
            var implementedInterfaceType = typeof(ICommand);
            var type = New.Common().Type
                .WithGenericArguments(implementedInterfaceType, implementedInterfaceType)
                .Creation;

            Assert.Throws<InvalidOperationException>(() => TypeExtensions.GetImplementedInterfacesOfSingleGenericTypeArgument(type));
        }
    }
}
