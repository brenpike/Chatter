using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Tests.Support
{
    /// <summary>
    /// A service that implements ONLY <see cref="IAsyncDisposable"/>. Registering it as scoped makes a DI scope
    /// that holds one throw <see cref="InvalidOperationException"/> when that scope is released synchronously,
    /// which is what lets a fact tell an asynchronous release from a synchronous one.
    /// </summary>
    public sealed class AsyncOnlyDisposableScopedService : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => default;
    }
}
