using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenCheckingIfTypeIsAValidMessageHandler : Testing.Core.Context
    {
        [Fact]
        public void MustReturnTrueIfNotGenericTypeAndImplementsIMessageHandlerWithMatchingTypeParam()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IMessageHandler<>))
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, typeToMatch);
            Assert.True(result);
        }

        [Fact]
        public void MustReturnTrueIfGenericTypeWithNonGenericArgumentsAndImplementsIMessageHandlerWithMatchingTypeParam()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IMessageHandler<>))
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .AsGeneric()
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, typeToMatch);
            Assert.True(result);
        }

        [Fact]
        public void MustReturnFalseIfGenericTypeWithGenericTypeArguments()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IMessageHandler<>))
                .WithGenericArguments(argType)
                .Creation;
            var genericParam = New.Common().Type
                .AsGenericTypeParameter().Creation;
            var type = New.Common().Type
                .AsGeneric()
                .WithImplementedInterfaces(closedGenericWithArgType)
                .WithGenericArguments(genericParam)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, typeToMatch);
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseIfTypeDoesNotImplementIMessageHandler()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IEnumerable<>))
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, typeToMatch);
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseIfGenericArgumentDoesNotMatch()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IMessageHandler<>))
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .AsGeneric()
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, typeof(IEvent));
            Assert.False(result);
        }

        [Fact]
        public void MustReturnFalseIfTypeToMatchIsNull()
        {
            var typeToMatch = typeof(ICommand);

            var argType = New.Common().Type
                .WithImplementedInterfaces(typeToMatch)
                .Creation;
            var closedGenericWithArgType = New.Common().Type
                .WithGenericTypeDef(typeof(IMessageHandler<>))
                .WithGenericArguments(argType)
                .Creation;
            var type = New.Common().Type
                .AsGeneric()
                .WithImplementedInterfaces(closedGenericWithArgType)
                .Creation;

            var result = CqrsExtensions.IsValidMessageHandler(type, null);
            Assert.False(result);
        }
    }
}
