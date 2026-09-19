using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingPersistanceTransaction
{
    // INVARIANT: PersistanceTransaction's invariant is a non-null inner transaction. Wrapping a null one produced a
    // handle whose every member threw NullReferenceException at the caller; the refusal is raised at creation
    // instead. Reached here through the module's [InternalsVisibleTo] grant to this test assembly.
    public class WhenCreatingPersistanceTransaction : Testing.Core.Context
    {
        [Fact]
        public void MustRefuseANullTransaction()
        {
            Action act = () => PersistanceTransaction.Create(null);

            act.Should().Throw<ArgumentNullException>()
                .Which.ParamName.Should().Be("dbContextTransaction");
        }
    }
}
