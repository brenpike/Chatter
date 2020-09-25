using Microsoft.EntityFrameworkCore;
using System;

namespace Chatter.Testing.Core.Creators.MessageBrokers.Reliability.EntityFramework
{
    public class DbContextCreator : Creator<DbContext>
    {
        public DbContextCreator(INewContext newContext, DbContext creation = null)
            : base(newContext, creation)
        {
            var ob = new DbContextOptionsBuilder<FakeContext>().UseInMemoryDatabase("FakeDB").Options;
            Creation = new FakeContext(ob);
        }

        private class FakeContext : DbContext
        {
            public FakeContext(DbContextOptions options) 
                : base(options)
            {
            }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<FakeEntity>(
                    b =>
                    {
                        b.Property(p => p.Id);
                        b.HasKey(p => p.Id);
                    });
            }
        }

        private class FakeEntity
        {
            public Guid Id { get; }
        }
    }
}
