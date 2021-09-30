using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenCheckingIfTypeIsGenericTypeWithNonGenericTypeParameters : Testing.Core.Context
    {
        [Fact]
        public void MustReturnTrueWhenTypeIsGenericAndNoTypeParametersAreGeneric()
        {
            var nonGenericType = New.Common().Type.Creation;
            var genericType = New.Common().Type
                .AsGeneric()
                .WithGenericArguments(nonGenericType).Creation;

            var result = TypeExtensions.IsGenericTypeWithNonGenericTypeParameters(genericType);
            Assert.True(result);
        }

        [Fact]
        public void MustReturnFalseWhenTypeIsNotGeneric()
        {
            var nonGenericType = New.Common().Type.Creation;
            var result = TypeExtensions.IsGenericTypeWithNonGenericTypeParameters(nonGenericType);
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseWhenAtLeastOneTypeParameterIsGeneric()
        {
            var genericTypeParameter = New.Common().Type.AsGeneric().Creation;
            var genericType = New.Common().Type
                .AsGeneric()
                .AsGenericTypeParameter()
                .WithGenericArguments(genericTypeParameter).Creation;

            var result = TypeExtensions.IsGenericTypeWithNonGenericTypeParameters(genericType);
            Assert.False(result);
        }

        [Fact]
        public void MustThrowIfTypeIsNull() 
            => Assert.Throws<NullReferenceException>(() => TypeExtensions.IsGenericTypeWithNonGenericTypeParameters(null));
    }
}
