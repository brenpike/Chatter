using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using System.Collections.Generic;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingTypeExtensions
{
    public class WhenCheckingIfImplementingOpenGenericTypeWithMatchingTypeParameter : Testing.Core.Context
    {
        [Fact]
        public void MustReturnTrueIfHasMatchingTypeParameter()
        {
            var typeToMatch = typeof(ICommand);
            var openGenericToMatch = typeof(IMessageHandler<>);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(openGenericToMatch)
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = TypeExtensions.IsImplementingOpenGenericTypeWithMatchingTypeParameter(type, openGenericToMatch, typeToMatch);
            Assert.True(result);
        }

        [Fact]
        public void MustReturnFalseIfTypeDoesNotHaveImplementedInterfacesThatMatchOpenGenericType()
        {
            var typeToMatch = typeof(ICommand);
            var openGenericToMatch = typeof(IMessageHandler<>);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(openGenericToMatch)
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = TypeExtensions.IsImplementingOpenGenericTypeWithMatchingTypeParameter(type, typeof(IEnumerable<>), typeToMatch);
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseIfNotImplementedInterfacesWithMatchingGenericTypeParameter()
        {
            var typeToMatch = typeof(ICommand);
            var openGenericToMatch = typeof(IMessageHandler<>);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(openGenericToMatch)
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = TypeExtensions.IsImplementingOpenGenericTypeWithMatchingTypeParameter(type, openGenericToMatch, typeof(IEvent));
            Assert.False(result);
        }
    }
}
