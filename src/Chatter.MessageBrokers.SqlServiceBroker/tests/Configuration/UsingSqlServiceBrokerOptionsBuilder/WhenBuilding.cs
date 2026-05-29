using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Configuration.UsingSqlServiceBrokerOptionsBuilder
{
    // Behavior-pinning tests: characterize SqlServiceBrokerOptionsBuilder and SqlServiceBrokerOptions
    // AS-IS, including latent quirks (NRE on a fluent setter before AddSqlServiceBrokerOptions, and the
    // divergent conversationLifetime default between the parameterized ctor and the builder overload).
    // INVARIANT: the const default message body type is "application/json; charset=utf-16" and matches
    // both the property initializer and what WithJsonBodyType() / the string overload assign.
    public class WhenBuilding : Testing.Core.Context
    {
        private const string DefaultMessageBodyType = "application/json; charset=utf-16";

        private static SqlServiceBrokerOptionsBuilder NewBuilder()
            => new SqlServiceBrokerOptionsBuilder(new ServiceCollection());

        // --- constructor guard ---

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesNull()
        {
            Action act = () => new SqlServiceBrokerOptionsBuilder(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustExposeProvidedServicesInstance()
        {
            var services = new ServiceCollection();
            new SqlServiceBrokerOptionsBuilder(services).Services.Should().BeSameAs(services);
        }

        // --- candidate finding: fluent setter before AddSqlServiceBrokerOptions dereferences null options ---

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithConnectionStringCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithConnectionString("Server=.;");
            act.Should().Throw<NullReferenceException>();
        }

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithMessageBodyTypeCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithMessageBodyType("text/plain");
            act.Should().Throw<NullReferenceException>();
        }

        [Fact]
        public void MustThrowNullReferenceExceptionWhenWithJsonBodyTypeCalledBeforeAddOptions()
        {
            Action act = () => NewBuilder().WithJsonBodyType();
            act.Should().Throw<NullReferenceException>();
        }

        // --- Build() guards ---

        [Fact]
        public void MustThrowArgumentNullExceptionWhenBuildingWithNoOptions()
        {
            Action act = () => NewBuilder().Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenConnectionStringBlank()
        {
            Action act = () => NewBuilder()
                .AddSqlServiceBrokerOptions(new SqlServiceBrokerOptions("   ", DefaultMessageBodyType))
                .Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenConnectionStringNull()
        {
            Action act = () => NewBuilder()
                .AddSqlServiceBrokerOptions(new SqlServiceBrokerOptions(null, DefaultMessageBodyType))
                .Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenMessageBodyTypeBlank()
        {
            Action act = () => NewBuilder()
                .AddSqlServiceBrokerOptions(new SqlServiceBrokerOptions("Server=.;", "   "))
                .Build();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustReturnSameOptionsInstanceFromBuildWhenValid()
        {
            var options = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            NewBuilder().AddSqlServiceBrokerOptions(options).Build().Should().BeSameAs(options);
        }

        // --- AddSqlServiceBrokerOptions overloads ---

        [Fact]
        public void MustStoreOptionsFromInstanceOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            var options = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            builder.AddSqlServiceBrokerOptions(options).Should().BeSameAs(builder);
            builder.Build().Should().BeSameAs(options);
        }

        [Fact]
        public void MustStoreOptionsFromFuncOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            var options = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            builder.AddSqlServiceBrokerOptions(() => options).Should().BeSameAs(builder);
            builder.Build().Should().BeSameAs(options);
        }

        [Fact]
        public void MustStoreOptionsFromConnectionStringOverloadAndReturnBuilder()
        {
            var builder = NewBuilder();
            builder.AddSqlServiceBrokerOptions("Server=.;").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustApplyConnectionStringOverloadDefaults()
        {
            var options = NewBuilder().AddSqlServiceBrokerOptions("Server=.;").Build();
            options.ConnectionString.Should().Be("Server=.;");
            options.MessageBodyType.Should().Be(DefaultMessageBodyType);
            options.ReceiverTimeoutInMilliseconds.Should().Be(-1);
            options.ConversationLifetimeInSeconds.Should().Be(0);
            options.ConversationEncryption.Should().BeFalse();
            options.CompressMessageBody.Should().BeTrue();
            options.CleanupOnEndConversation.Should().BeFalse();
            options.EndConversationAfterDispatch.Should().BeTrue();
        }

        // --- fluent setters map onto the options and return the builder ---

        [Fact]
        public void MustApplyWithConnectionStringAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.WithConnectionString("Server=other;").Should().BeSameAs(builder);
            builder.Build().ConnectionString.Should().Be("Server=other;");
        }

        [Fact]
        public void MustApplyWithMessageBodyTypeAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.WithMessageBodyType("text/plain").Should().BeSameAs(builder);
            builder.Build().MessageBodyType.Should().Be("text/plain");
        }

        [Fact]
        public void MustApplyWithJsonBodyTypeAsDefaultConstAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;").WithMessageBodyType("text/plain");
            builder.WithJsonBodyType().Should().BeSameAs(builder);
            builder.Build().MessageBodyType.Should().Be("application/json; charset=utf-16");
        }

        [Fact]
        public void MustApplyWithReceiverTimeoutAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.WithReceiverTimeout(1234).Should().BeSameAs(builder);
            builder.Build().ReceiverTimeoutInMilliseconds.Should().Be(1234);
        }

        [Fact]
        public void MustApplyWithConversationLifetimeAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.WithConversationLifetime(99).Should().BeSameAs(builder);
            builder.Build().ConversationLifetimeInSeconds.Should().Be(99);
        }

        [Fact]
        public void MustApplyUseConversationEncryptionAsTrueAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.UseConversationEncryption().Should().BeSameAs(builder);
            builder.Build().ConversationEncryption.Should().BeTrue();
        }

        [Fact]
        public void MustApplyWithMessageBodyCompressionAsTrueAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;", compressMessageBody: false);
            builder.WithMessageBodyCompression().Should().BeSameAs(builder);
            builder.Build().CompressMessageBody.Should().BeTrue();
        }

        [Fact]
        public void MustApplyWithConversationCleanupAsTrueAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.WithConversationCleanup().Should().BeSameAs(builder);
            builder.Build().CleanupOnEndConversation.Should().BeTrue();
        }

        [Fact]
        public void MustApplyEndConversationAfterDispatchAndReturnBuilder()
        {
            var builder = NewBuilder().AddSqlServiceBrokerOptions("Server=.;");
            builder.EndConversationAfterDispatch(false).Should().BeSameAs(builder);
            builder.Build().EndConversationAfterDispatch.Should().BeFalse();
        }

        // --- SqlServiceBrokerOptions default divergence (candidate finding) ---
        // The parameterized ctor defaults conversationLifetimeInSeconds to int.MaxValue, while the
        // property initializer and the builder's connection-string overload both default it to 0.

        [Fact]
        public void MustDefaultCtorConversationLifetimeToIntMaxValue()
        {
            var options = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            options.ConversationLifetimeInSeconds.Should().Be(int.MaxValue);
        }

        [Fact]
        public void MustDefaultBuilderOverloadConversationLifetimeToZero()
        {
            var options = NewBuilder().AddSqlServiceBrokerOptions("Server=.;").Build();
            options.ConversationLifetimeInSeconds.Should().Be(0);
        }

        [Fact]
        public void MustHaveDivergentCtorAndBuilderConversationLifetimeDefaults()
        {
            var ctorOptions = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            var builderOptions = NewBuilder().AddSqlServiceBrokerOptions("Server=.;").Build();
            ctorOptions.ConversationLifetimeInSeconds
                .Should().NotBe(builderOptions.ConversationLifetimeInSeconds);
        }

        [Fact]
        public void MustApplyAllCtorDefaultsExceptConversationLifetime()
        {
            var options = new SqlServiceBrokerOptions("Server=.;", DefaultMessageBodyType);
            options.ReceiverTimeoutInMilliseconds.Should().Be(-1);
            options.ConversationEncryption.Should().BeFalse();
            options.CompressMessageBody.Should().BeTrue();
            options.CleanupOnEndConversation.Should().BeFalse();
            options.EndConversationAfterDispatch.Should().BeTrue();
        }
    }
}
