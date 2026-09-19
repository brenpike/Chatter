using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    public sealed class ScopedReceivedMessageDispatcher : IReceivedMessageDispatcher
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public ScopedReceivedMessageDispatcher(IServiceScopeFactory serviceScopeFactory)
            => _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));

        async Task IReceivedMessageDispatcher.DispatchAsync<TMessage>(TMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            // INVARIANT: the release is asynchronous because a scoped member of the dispatch graph may implement
            // only IAsyncDisposable, which a synchronous release refuses.
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
            messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());
            await dispatcher.Dispatch(payload, messageContext);
        }
    }
}
