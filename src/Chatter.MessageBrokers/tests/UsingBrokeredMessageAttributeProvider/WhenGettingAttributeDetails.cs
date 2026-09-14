using FluentAssertions;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingBrokeredMessageAttributeProvider
{
    public class WhenGettingAttributeDetails : Testing.Core.Context
    {
        private readonly BrokeredMessageAttributeProvider _sut = new BrokeredMessageAttributeProvider();

        [BrokeredMessage(
            sendingPath: "sending",
            receivingPath: "receiving",
            errorQueueName: "errorQueue",
            messageDescription: "description",
            infrastructureType: "infra")]
        private class DecoratedMessage { }

        [BrokeredMessage(sendingPath: "sending", receivingPath: "receiving")]
        private class DecoratedMessageWithoutDescription { }

        private class UndecoratedMessage { }

        [Fact]
        public void MustGetMessageNameFromSendingPathViaGeneric()
            => _sut.GetMessageName<DecoratedMessage>().Should().Be("sending");

        [Fact]
        public void MustGetMessageNameFromSendingPathViaType()
            => _sut.GetMessageName(typeof(DecoratedMessage)).Should().Be("sending");

        [Fact]
        public void MustGetReceiverNameFromReceivingPath()
            => _sut.GetReceiverName<DecoratedMessage>().Should().Be("receiving");

        [Fact]
        public void MustGetErrorQueueName()
            => _sut.GetErrorQueueName<DecoratedMessage>().Should().Be("errorQueue");

        [Fact]
        public void MustGetInfrastructureType()
            => _sut.GetInfrastructureType<DecoratedMessage>().Should().Be("infra");

        [Fact]
        public void MustGetMessageDescriptionWhenDescriptionIsSet()
            => _sut.GetBrokeredMessageDescription<DecoratedMessage>().Should().Be("description");

        [Fact]
        public void MustFallBackToReceiverNameForDescriptionWhenDescriptionIsNotSet()
            => _sut.GetBrokeredMessageDescription<DecoratedMessageWithoutDescription>().Should().Be("receiving");

        [Fact]
        public void MustReturnNullMessageNameWhenTypeIsNotDecorated()
            => _sut.GetMessageName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullMessageNameViaTypeWhenTypeIsNotDecorated()
            => _sut.GetMessageName(typeof(UndecoratedMessage)).Should().BeNull();

        [Fact]
        public void MustReturnNullReceiverNameWhenTypeIsNotDecorated()
            => _sut.GetReceiverName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullErrorQueueNameWhenTypeIsNotDecorated()
            => _sut.GetErrorQueueName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullInfrastructureTypeWhenTypeIsNotDecorated()
            => _sut.GetInfrastructureType<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullDescriptionWhenTypeIsNotDecorated()
            => _sut.GetBrokeredMessageDescription<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnSameSendingPathAcrossInstancesAndOverloads()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetMessageName<DecoratedMessage>().Should().Be("sending");
            first.GetMessageName(typeof(DecoratedMessage)).Should().Be("sending");
            second.GetMessageName<DecoratedMessage>().Should().Be("sending");
            second.GetMessageName(typeof(DecoratedMessage)).Should().Be("sending");
        }

        [Fact]
        public void MustReturnSameReceiverNameAcrossInstances()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetReceiverName<DecoratedMessage>().Should().Be("receiving");
            second.GetReceiverName<DecoratedMessage>().Should().Be("receiving");
        }

        [Fact]
        public void MustReturnSameErrorQueueNameAcrossInstances()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetErrorQueueName<DecoratedMessage>().Should().Be("errorQueue");
            second.GetErrorQueueName<DecoratedMessage>().Should().Be("errorQueue");
        }

        [Fact]
        public void MustReturnSameDescriptionAcrossInstances()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetBrokeredMessageDescription<DecoratedMessage>().Should().Be("description");
            second.GetBrokeredMessageDescription<DecoratedMessage>().Should().Be("description");
        }

        [Fact]
        public void MustReturnSameInfrastructureTypeAcrossInstances()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetInfrastructureType<DecoratedMessage>().Should().Be("infra");
            second.GetInfrastructureType<DecoratedMessage>().Should().Be("infra");
        }

        [Fact]
        public void MustFallBackToReceiverNameForDescriptionAcrossInstancesWhenDescriptionIsNotSet()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetBrokeredMessageDescription<DecoratedMessageWithoutDescription>().Should().Be("receiving");
            second.GetBrokeredMessageDescription<DecoratedMessageWithoutDescription>().Should().Be("receiving");
        }

        [Fact]
        public void MustReturnEqualMessageNameWhenTypeOverloadCalledFirstThenGenericOnDifferentInstance()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            var viaType = first.GetMessageName(typeof(DecoratedMessage));
            var viaGeneric = second.GetMessageName<DecoratedMessage>();

            viaGeneric.Should().Be(viaType);
        }

        [Fact]
        public void MustReturnNullForAllMembersAcrossInstancesWhenTypeIsNotDecorated()
        {
            var first = new BrokeredMessageAttributeProvider();
            var second = new BrokeredMessageAttributeProvider();

            first.GetMessageName<UndecoratedMessage>().Should().BeNull();
            second.GetMessageName<UndecoratedMessage>().Should().BeNull();

            first.GetMessageName(typeof(UndecoratedMessage)).Should().BeNull();
            second.GetMessageName(typeof(UndecoratedMessage)).Should().BeNull();

            first.GetReceiverName<UndecoratedMessage>().Should().BeNull();
            second.GetReceiverName<UndecoratedMessage>().Should().BeNull();

            first.GetErrorQueueName<UndecoratedMessage>().Should().BeNull();
            second.GetErrorQueueName<UndecoratedMessage>().Should().BeNull();

            first.GetInfrastructureType<UndecoratedMessage>().Should().BeNull();
            second.GetInfrastructureType<UndecoratedMessage>().Should().BeNull();
        }

        [Fact]
        public void MustReturnNullDescriptionForUndecoratedTypeAfterNullSafeMemberCachesNullOnRepeatedCalls()
        {
            var nullSafeCaller = new BrokeredMessageAttributeProvider();
            nullSafeCaller.GetMessageName<UndecoratedMessage>().Should().BeNull();

            var descriptionCaller = new BrokeredMessageAttributeProvider();

            descriptionCaller.GetBrokeredMessageDescription<UndecoratedMessage>().Should().BeNull();
            descriptionCaller.GetBrokeredMessageDescription<UndecoratedMessage>().Should().BeNull();
        }

        [Fact]
        public void MustNotKeepCollectibleMessageTypeAliveAfterGettingItsDetails()
        {
            var collectibleMessageType = GetDetailsOfCollectibleMessageType(_sut);

            for (var attempt = 0; attempt < 10 && collectibleMessageType.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            collectibleMessageType.IsAlive.Should().BeFalse();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference GetDetailsOfCollectibleMessageType(BrokeredMessageAttributeProvider sut)
        {
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"CollectibleMessages{Guid.NewGuid():N}"),
                AssemblyBuilderAccess.RunAndCollect);
            var typeBuilder = assemblyBuilder
                .DefineDynamicModule("CollectibleMessages")
                .DefineType("CollectibleMessage", TypeAttributes.Public | TypeAttributes.Class);
            var attributeConstructor = typeof(BrokeredMessageAttribute).GetConstructor(
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) });
            typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                attributeConstructor,
                new object[] { "collectible-sending", "collectible-receiving", null, null, "", null }));
            var messageType = typeBuilder.CreateType();

            messageType.IsCollectible.Should().BeTrue();
            sut.GetMessageName(messageType).Should().Be("collectible-sending");

            return new WeakReference(messageType);
        }
    }
}
