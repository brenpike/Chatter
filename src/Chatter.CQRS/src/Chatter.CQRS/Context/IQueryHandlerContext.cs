using System.Threading;

namespace Chatter.CQRS.Context
{
	/// <summary>
	/// Context that is passed to <see cref="IQueryHandler{TQuery,TResult}"/> when an <see cref="IQuery"/> is handled.
	/// </summary>
	public interface IQueryHandlerContext : IContainContext
	{
		CancellationToken CancellationToken { get; }
	}
}
