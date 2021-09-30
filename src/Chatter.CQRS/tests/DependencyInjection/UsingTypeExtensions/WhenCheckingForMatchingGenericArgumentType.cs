using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenCheckingForMatchingGenericArgumentType : Testing.Core.Context
    {
        [Fact]
        public void MustReturnTrueIfGenericImplementedInterfaceHasMatchingType()
        {
            var genericArgumentType = New.Common().Type
                .WithImplementedInterfaces(typeof(ICommand)).Creation;
            var type = New.Common().Type
                .WithGenericArguments(genericArgumentType)
                .Creation;
            var types = new Type[] { type };

            var result = TypeExtensions.HasMatchingGenericArgumentType(types, typeof(ICommand));
            Assert.True(result);
        }

        [Fact]
        public void MustReturnFalseIfGenericImplementedInterfaceDoesNotHaveMatchingType()
        {
            var genericArgumentType = New.Common().Type;
            var type = New.Common().Type
                .WithGenericArguments(genericArgumentType)
                .Creation;
            var types = new Type[] { type };

            var result = TypeExtensions.HasMatchingGenericArgumentType(types, typeof(ICommand));
            Assert.False(result);
        }

        [Fact]
        public void MustThrowIfEnuerambleOfTypesIsNull() 
            => Assert.Throws<ArgumentNullException>(() => TypeExtensions.HasMatchingGenericArgumentType(null, typeof(ICommand)));

        [Fact]
        public void MustReturnFalseIfEnumerableOfTypeIsEmpty()
        {
            var types = new Type[] { };

            var result = TypeExtensions.HasMatchingGenericArgumentType(types, typeof(ICommand));
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseIfTypeParameterTypeToMatchIsNull()
        {
            var genericArgumentType = New.Common().Type
                .WithImplementedInterfaces(typeof(ICommand)).Creation;
            var type = New.Common().Type
                .WithGenericArguments(genericArgumentType)
                .Creation;
            var types = new Type[] { type };

            var result = TypeExtensions.HasMatchingGenericArgumentType(types, null);
            Assert.False(result);
        }
    }
}
