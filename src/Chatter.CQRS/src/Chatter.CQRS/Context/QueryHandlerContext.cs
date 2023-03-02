using System.Threading;

namespace Chatter.CQRS.Context
{
	/// <summary>
	/// Context that is passed to <see cref="IQueryHandler{TQuery,TResult}"/> when an <see cref="IQuery"/> is handled.
	/// </summary>
	public class QueryHandlerContext : IQueryHandlerContext
	{
		public QueryHandlerContext(CancellationToken cancellationToken)
		{
			Container = new ContextContainer();
			CancellationToken = cancellationToken;
		}

		public QueryHandlerContext()
			: this(default)
		{ }

		/// <summary>
		/// A context container that support extensibility by holding additional context
		/// </summary>
		public ContextContainer Container { get; private set; }

		public CancellationToken CancellationToken { get; private set; }
	}
}
