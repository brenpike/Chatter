using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public static class ExternalDispatcherExtensions
    {
        //public static Task Send<TMessage>(this IExternalDispatcher externalDispatcher, TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
        //{
        //    if (externalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
        //    {
        //        return brokeredMessageDispatcher.Send(message, destinationPath, transactionContext, options);
        //    }

        //    return Task.CompletedTask;
        //}

        //public static Task Send<TMessage>(this IExternalDispatcher externalDispatcher, TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
        //{
        //    if (externalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
        //    {
        //        return brokeredMessageDispatcher.Send(message, transactionContext, options);
        //    }

        //    return Task.CompletedTask;
        //}

        //public static Task Publish<TMessage>(this IExternalDispatcher externalDispatcher, TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
        //{
        //    if (externalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
        //    {
        //        return brokeredMessageDispatcher.Publish(message, destinationPath, transactionContext, options);
        //    }

        //    return Task.CompletedTask;
        //}

        //public static Task Publish<TMessage>(this IExternalDispatcher externalDispatcher, TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
        //{
        //    if (externalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
        //    {
        //        return brokeredMessageDispatcher.Publish(message, transactionContext, options);
        //    }

        //    return Task.CompletedTask;
        //}
    }
}
