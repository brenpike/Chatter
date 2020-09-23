using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    class ExecutionStrategyBehavior<TMessage, TContext> : ICommandBehavior<TMessage> where TMessage : IMessage where TContext : DbContext
    {
        private readonly TContext _context;

        public ExecutionStrategyBehavior(TContext context) 
            => _context = context ?? throw new ArgumentNullException(nameof(context));

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(async () =>
            {
                await next();
            });
        }
    }
}
