using System;
using System.Collections.Generic;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Contains context used to extend functionality
    /// </summary>
    /// <remarks>
    /// A <see cref="ContextContainer"/> is NOT synchronized. A container is owned by exactly one message dispatch
    /// and must be used by one thread at a time. Sharing a single container across concurrent threads - for example
    /// by dispatching from within a handler without awaiting, or by capturing a container in a background task -
    /// is unsupported and can corrupt the underlying dictionary.
    /// </remarks>
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
        /// <returns>
        /// The value already present in the container - including a stored <see langword="null"/> - or <see cref="default{T}"/>,
        /// which is stored in the container before being returned.
        /// </returns>
        public T GetOrDefault<T>()
            => GetOrAdd<T>(() => default);

        /// <summary>
        /// Gets context of type <typeparamref name="T"/> from the container. If it doesn't exist, uses a factory method to create a new instance
        /// of <typeparamref name="T"/>, adds to the container and returns the value.
        /// </summary>
        /// <typeparam name="T">The type of context to get or add.</typeparam>
        /// <param name="factoryMethod">The factory to create <typeparamref name="T"/> if not found in the container.</param>
        /// <returns>
        /// The value already present in the container - including a stored <see langword="null"/> - or the value created by
        /// <paramref name="factoryMethod"/>. <paramref name="factoryMethod"/> is invoked only when no value is present for
        /// <typeparamref name="T"/>, and its result is stored even when it is <see langword="null"/> or a default value type.
        /// </returns>
        public T GetOrAdd<T>(Func<T> factoryMethod)
        {
            if (TryGet<T>(out var existingValue))
            {
                return existingValue;
            }

            var createdValue = factoryMethod();
            Include(createdValue);
            return createdValue;
        }

        /// <summary>
        /// Gets context of type <typeparamref name="T"/> from the container. If it doesn't exist, creates a new instance
        /// of <typeparamref name="T"/>, adds to the container and returns the value.
        /// </summary>
        /// <typeparam name="T">The type of context to get or add.</typeparam>
        /// <returns>
        /// The non-<see langword="null"/> value already present in the container, otherwise a new instance of
        /// <typeparamref name="T"/> which is stored in the container before being returned. Unlike <see cref="GetOrAdd{T}(Func{T})"/>,
        /// a stored <see langword="null"/> is replaced with a new instance.
        /// </returns>
        public T GetOrNew<T>() where T : class, new()
        {
            if (TryGet<T>(out var existingValue) && existingValue is not null)
            {
                return existingValue;
            }

            var createdValue = new T();
            Include(createdValue);
            return createdValue;
        }
    }
}
