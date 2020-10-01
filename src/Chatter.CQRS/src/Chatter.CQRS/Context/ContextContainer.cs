using System;
using System.Collections.Generic;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Contains context used to extend functionality
    /// </summary>
    public class ContextContainer
    {
        private readonly IDictionary<string, object> _context = new Dictionary<string, object>();
        private readonly ContextContainer _inheritedContext;

        /// <summary>
        /// Creates a new Context Container.
        /// </summary>
        /// <param name="inheritedContext">An optional <see cref="ContextContainer"/> which allows its contained context to be accessed via this container</param>
        public ContextContainer(ContextContainer inheritedContext = null) 
            => _inheritedContext = inheritedContext;

        /// <summary>
        /// Get context of <typeparamref name="T"/> from container.
        /// </summary>
        /// <typeparam name="T">The tyoe of context to find in the container</typeparam>
        /// <exception cref="KeyNotFoundException">If no context of <typeparamref name="T"/> is found in the container</exception>
        /// <returns>The context of <typeparamref name="T"/> if found in the container</returns>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        public T Get<T>() 
            => Get<T>(typeof(T).FullName);

        /// <summary>
        /// Get context of <typeparamref name="T"/> from container.
        /// </summary>
        /// <typeparam name="T">The type of context to find in the container</typeparam>
        /// <exception cref="KeyNotFoundException">If no context of <typeparamref name="T"/> is found in the container</exception>
        /// <param name="fullQualifiedNamespaceOfType">The fully qualified type name of the context object to get from the container</param>
        /// <returns>The context of <typeparamref name="T"/> if found in the container</returns>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        public T Get<T>(string fullQualifiedNamespaceOfType)
        {
            if (!TryGet(fullQualifiedNamespaceOfType, out T result))
            {
                throw new KeyNotFoundException("No item found in container with key: " + fullQualifiedNamespaceOfType);
            }

            return result;
        }

        /// <summary>
        /// Attempts to get context of <typeparamref name="T"/> from container.
        /// </summary>
        /// <typeparam name="T">The type of context to find in the container</typeparam>
        /// <param name="result">The context of <typeparamref name="T"/> if it exists in the container</param>
        /// <returns>True if the context of <typeparamref name="T"/> was found in the container, false otherwise</returns>
        /// <remarks>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        /// </remarks>
        public bool TryGet<T>(out T result) 
            => TryGet(typeof(T).FullName, out result);

        /// <summary>
        /// Attempts to get context of <typeparamref name="T"/> from container.
        /// </summary>
        /// <typeparam name="T">The type of context to find in the container</typeparam>
        /// <param name="fullQualifiedNamespaceOfType">The fully qualified type name of the context object to get from the container</param>
        /// <param name="result">The context of <typeparamref name="T"/> if it exists in the container</param>
        /// <returns>True if the context of <typeparamref name="T"/> was found in the container, false otherwise</returns>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        public bool TryGet<T>(string fullQualifiedNamespaceOfType, out T result)
        {
            if (_context.TryGetValue(fullQualifiedNamespaceOfType, out var value))
            {
                result = (T)value;
                return true;
            }

            if (_inheritedContext != null)
            {
                return _inheritedContext.TryGet(fullQualifiedNamespaceOfType, out result);
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Includes context of <typeparamref name="T"/> in the container
        /// </summary>
        /// <typeparam name="T">The type of context to be included</typeparam>
        /// <param name="t">The context to include</param>
        public void Include<T>(T t) 
            => Include(typeof(T).FullName, t);

        /// <summary>
        /// Includes context of <typeparamref name="T"/> in the container
        /// </summary>
        /// <typeparam name="T">The type of context to be included</typeparam>
        /// <param name="fullQualifiedNamespaceOfType">The fully qualified type name of the context object to get from the container</param>
        /// <param name="t">The context to include</param>
        public void Include<T>(string fullQualifiedNamespaceOfType, T t) 
            => _context[fullQualifiedNamespaceOfType] = t;

        /// <summary>
        /// Gets context of type <typeparamref name="T"/> from the container. If it doesn't exist, uses <see cref="default{T}"/> to create a new instance
        /// of <typeparamref name="T"/>, adds to the container and returns the value.
        /// </summary>
        /// <typeparam name="T">The type of context to get or add.</typeparam>
        /// <returns>The value retrieved from context or <see cref="default{T}"/>.</returns>
        public T GetOrDefault<T>()
            => GetOrAdd<T>(() => default);

        /// <summary>
        /// Gets context of type <typeparamref name="T"/> from the container. If it doesn't exist, uses a factory method to create a new instance
        /// of <typeparamref name="T"/>, adds to the container and returns the value.
        /// </summary>
        /// <typeparam name="T">The type of context to get or add.</typeparam>
        /// <param name="factoryMethod">The factory to create <typeparamref name="T"/> if not found in the container.</param>
        /// <returns>The value retrieved from context or created by <paramref name="factoryMethod"/>.</returns>
        public T GetOrAdd<T>(Func<T> factoryMethod)
        {
            TryGet<T>(out var tryGetValue);
            if (tryGetValue is null)
            {
                tryGetValue = factoryMethod();
                Include(tryGetValue);
            }
            return tryGetValue;
        }

        /// <summary>
        /// Gets context of type <typeparamref name="T"/> from the container. If it doesn't exist, creates a new instance
        /// of <typeparamref name="T"/>, adds to the container and returns the value.
        /// </summary>
        /// <typeparam name="T">The type of context to get or add.</typeparam>
        /// <returns>The value retrieved from context or a new instance of <typeparamref name="T"/>.</returns>
        public T GetOrNew<T>() where T : class, new()
            => GetOrAdd(() => new T());
    }
}
