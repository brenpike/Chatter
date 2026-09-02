using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.DependencyInjection;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingChangeFeedObjectNames
{
    /// <summary>
    /// Pins the single derivation shared by the Change Feed Migration overloads and the change feed
    /// receiver registration, so a configured queue or dead letter service name reaches the installed
    /// Service Broker topology instead of diverging from it.
    /// </summary>
    public class WhenDeriving : Testing.Core.Context
    {
        private static ChangeFeedObjectNames DeriveNames(
            string changeFeedQueueName = null,
            string changeFeedDeadLetterServiceName = null)
            => ChangeFeedObjectNames.DeriveFrom(typeof(FakeRowData), CreateOptions(changeFeedQueueName, changeFeedDeadLetterServiceName));

        private static SqlChangeFeedOptions CreateOptions(
            string changeFeedQueueName = null,
            string changeFeedDeadLetterServiceName = null)
            => new SqlChangeFeedOptions(
                "connection-string",
                "database",
                "table",
                changeFeedQueueName: changeFeedQueueName,
                changeFeedDeadLetterQueueName: changeFeedDeadLetterServiceName);

        [Fact]
        public void MustThrowArgumentNullExceptionWhenRowChangedDataTypeIsNull()
            => FluentActions.Invoking(() => ChangeFeedObjectNames.DeriveFrom(null, CreateOptions()))
                .Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("rowChangedDataType");

        [Fact]
        public void MustDeriveConversationQueueNameFromRowChangedDataTypeWhenNotConfigured()
            => DeriveNames().ConversationQueueName.Should().Be("Chatter_Queue_FakeRowData");

        [Fact]
        public void MustDeriveConversationServiceNameFromRowChangedDataType()
            => DeriveNames().ConversationServiceName.Should().Be("Chatter_Service_FakeRowData");

        [Fact]
        public void MustDeriveConversationDeadLetterQueueNameFromRowChangedDataType()
            => DeriveNames().ConversationDeadLetterQueueName.Should().Be("Chatter_DeadLetterQueue_FakeRowData");

        [Fact]
        public void MustDeriveConversationDeadLetterServiceNameFromRowChangedDataTypeWhenNotConfigured()
            => DeriveNames().ConversationDeadLetterServiceName.Should().Be("Chatter_DeadLetterService_FakeRowData");

        [Fact]
        public void MustDeriveConversationTriggerNameFromRowChangedDataType()
            => DeriveNames().ConversationTriggerName.Should().Be("Chatter_ChangeFeedTrigger_FakeRowData");

        [Fact]
        public void MustDeriveInstallChangeFeedStoredProcNameFromRowChangedDataType()
            => DeriveNames().InstallChangeFeedStoredProcName.Should().Be("Chatter_InstallChangeFeed_FakeRowData");

        [Fact]
        public void MustDeriveUninstallChangeFeedStoredProcNameFromRowChangedDataType()
            => DeriveNames().UninstallChangeFeedStoredProcName.Should().Be("Chatter_UninstallChangeFeed_FakeRowData");

        [Fact]
        public void MustUseConfiguredChangeFeedQueueNameAsConversationQueueName()
            => DeriveNames(changeFeedQueueName: "CustomQueue")
                .ConversationQueueName.Should().Be("CustomQueue");

        [Fact]
        public void MustUseConfiguredChangeFeedDeadLetterServiceNameAsConversationDeadLetterServiceName()
            => DeriveNames(changeFeedDeadLetterServiceName: "CustomDeadLetterService")
                .ConversationDeadLetterServiceName.Should().Be("CustomDeadLetterService");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustFallBackToDerivedConversationQueueNameWhenConfiguredQueueNameIsNullOrWhitespace(string changeFeedQueueName)
            => DeriveNames(changeFeedQueueName: changeFeedQueueName)
                .ConversationQueueName.Should().Be("Chatter_Queue_FakeRowData");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustFallBackToDerivedConversationDeadLetterServiceNameWhenConfiguredIsNullOrWhitespace(string changeFeedDeadLetterServiceName)
            => DeriveNames(changeFeedDeadLetterServiceName: changeFeedDeadLetterServiceName)
                .ConversationDeadLetterServiceName.Should().Be("Chatter_DeadLetterService_FakeRowData");

        [Fact]
        public void MustKeepConversationServiceNameDerivedWhenChangeFeedQueueNameIsConfigured()
            => DeriveNames(changeFeedQueueName: "CustomQueue")
                .ConversationServiceName.Should().Be("Chatter_Service_FakeRowData");

        [Fact]
        public void MustKeepConversationDeadLetterQueueNameDerivedWhenDeadLetterServiceNameIsConfigured()
            => DeriveNames(changeFeedDeadLetterServiceName: "CustomDeadLetterService")
                .ConversationDeadLetterQueueName.Should().Be("Chatter_DeadLetterQueue_FakeRowData");

        [Fact]
        public void MustDeriveDefaultConversationQueueNameWhenOptionsIsNull()
            => ChangeFeedObjectNames.DeriveFrom(typeof(FakeRowData), null)
                .ConversationQueueName.Should().Be("Chatter_Queue_FakeRowData");

        [Fact]
        public void MustDeriveDefaultConversationDeadLetterServiceNameWhenOptionsIsNull()
            => ChangeFeedObjectNames.DeriveFrom(typeof(FakeRowData), null)
                .ConversationDeadLetterServiceName.Should().Be("Chatter_DeadLetterService_FakeRowData");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredDeadLetterServiceNameEqualsDerivedConversationServiceName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedDeadLetterServiceName: "Chatter_Service_FakeRowData"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationServiceName*ConversationDeadLetterServiceName*Chatter_Service_FakeRowData*");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredQueueNameEqualsDerivedConversationDeadLetterQueueName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "Chatter_DeadLetterQueue_FakeRowData"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationQueueName*ConversationDeadLetterQueueName*Chatter_DeadLetterQueue_FakeRowData*");

        [Fact]
        public void MustNotThrowWhenConfiguredQueueNameEqualsConfiguredDeadLetterServiceName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "SharedName", changeFeedDeadLetterServiceName: "SharedName"))
                .Should().NotThrow();

        [Fact]
        public void MustNotThrowWhenConfiguredQueueNameEqualsDerivedConversationServiceName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "Chatter_Service_FakeRowData"))
                .Should().NotThrow();

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredDeadLetterServiceNameCaseInsensitivelyEqualsDerivedConversationServiceName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedDeadLetterServiceName: "chatter_service_fakerowdata"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationServiceName*ConversationDeadLetterServiceName*chatter_service_fakerowdata*");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredQueueNameCaseInsensitivelyEqualsDerivedConversationDeadLetterQueueName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "chatter_deadletterqueue_fakerowdata"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationQueueName*ConversationDeadLetterQueueName*chatter_deadletterqueue_fakerowdata*");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredQueueNameEqualsDerivedConversationTriggerName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "Chatter_ChangeFeedTrigger_FakeRowData"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationQueueName*ConversationTriggerName*Chatter_ChangeFeedTrigger_FakeRowData*");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredQueueNameEqualsDerivedInstallChangeFeedStoredProcName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "Chatter_InstallChangeFeed_FakeRowData"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationQueueName*InstallChangeFeedStoredProcName*Chatter_InstallChangeFeed_FakeRowData*");

        [Fact]
        public void MustThrowChangeFeedObjectNameCollisionExceptionWhenConfiguredQueueNameEqualsDerivedUninstallChangeFeedStoredProcName()
            => FluentActions.Invoking(() => DeriveNames(changeFeedQueueName: "Chatter_UninstallChangeFeed_FakeRowData"))
                .Should().Throw<ChangeFeedObjectNameCollisionException>()
                .WithMessage("*ConversationQueueName*UninstallChangeFeedStoredProcName*Chatter_UninstallChangeFeed_FakeRowData*");
    }
}
