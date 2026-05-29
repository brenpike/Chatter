using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingReceivedMessage
{
    // Behavior-pinning tests: characterize the ReceivedMessage constructor AS-IS. It assigns every
    // ctor argument verbatim to its getter-only property with no validation or guard, so null bodies
    // and null strings are stored as-is.
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustAssignAllPropertiesVerbatim()
        {
            var convGroupHandle = Guid.NewGuid();
            var convHandle = Guid.NewGuid();
            var body = Encoding.Unicode.GetBytes("payload");

            var sut = new ReceivedMessage(convGroupHandle,
                                          convHandle,
                                          7L,
                                          "service-name",
                                          "service-contract-name",
                                          "message-type-name",
                                          body);

            sut.ConvGroupHandle.Should().Be(convGroupHandle);
            sut.ConvHandle.Should().Be(convHandle);
            sut.MessageSeqNo.Should().Be(7L);
            sut.ServiceName.Should().Be("service-name");
            sut.ServiceContractName.Should().Be("service-contract-name");
            sut.MessageTypeName.Should().Be("message-type-name");
            sut.Body.Should().BeSameAs(body);
        }

        // Pins the absence of a null guard: a null body is stored as-is.
        [Fact]
        public void MustStoreNullBodyAsIs()
        {
            var sut = new ReceivedMessage(Guid.Empty,
                                          Guid.Empty,
                                          0L,
                                          "service",
                                          "contract",
                                          "type",
                                          null);

            sut.Body.Should().BeNull();
        }

        // Pins the absence of a null guard: null string arguments are stored as-is.
        [Fact]
        public void MustStoreNullStringsAsIs()
        {
            var sut = new ReceivedMessage(Guid.Empty,
                                          Guid.Empty,
                                          0L,
                                          null,
                                          null,
                                          null,
                                          Array.Empty<byte>());

            sut.ServiceName.Should().BeNull();
            sut.ServiceContractName.Should().BeNull();
            sut.MessageTypeName.Should().BeNull();
        }
    }
}
