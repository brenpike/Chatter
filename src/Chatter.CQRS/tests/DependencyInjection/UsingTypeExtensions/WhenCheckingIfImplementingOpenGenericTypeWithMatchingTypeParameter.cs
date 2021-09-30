using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.Common;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenCheckingIfImplementingOpenGenericTypeWithMatchingTypeParameter : Testing.Core.Context
    {
        [Fact]
        public void MustReturnTrueIfTypeHasImplementedInterfacesThatMatchOpenGenericTypeWithMatchingGenericArgumentType()
        {

        }

        [Fact]
        public void MustReturnFalseIfTypeDoesNotHaveImplementedInterfacesThatMatchOpenGenericType()
        {

        }

        [Fact]
        public void MustReturnFalseIfImplementedInterfacesDoNotMatchOpenGenericType()
        {

        }
    }
}
