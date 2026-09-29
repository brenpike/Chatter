using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    /// <summary>
    /// Marks a transport setting whose value may appear in an exception message. A setting without this attribute is
    /// rendered as "(redacted)" by <see cref="SqlServiceBrokerTransportSettings"/>, so a setting added later stays hidden
    /// until someone decides its value is not a credential.
    /// </summary>
    // INVARIANT: SqlServiceBrokerOptions.ConnectionString carries no SafeToPrintAttribute, because it is a credential.
    // Oracle: WhenSnapshotting.MustRenderBothConnectionStringValuesAsRedactedWhenConnectionStringsDiffer. Mutation: marking
    // ConnectionString with this attribute. Measured: that fact and
    // WhenAddingSqlServiceBroker.MustRefuseASecondCallWhoseConnectionStringDiverges go red, and nothing else in the
    // SqlServiceBroker test project.
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class SafeToPrintAttribute : Attribute
    {
    }
}
