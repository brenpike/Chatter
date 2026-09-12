using System;
using System.Collections.Generic;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Contains context used to extend functionality
    /// </summary>
    /// <remarks>
    /// A <see cref="ContextContainer"/> stores a plain <see cref="Dictionary{TKey, TValue}"/>, takes no lock and
    /// provides no synchronization of any kind on any of its members.
    /// Never use one container from two threads at the same time: concurrent use is undefined and can corrupt the
    /// underlying dictionary. Await each nested dispatch before starting the next, and do not capture a Message
    /// Context - or its container - into work that runs alongside its dispatch. A nested dispatch that runs against
    /// the caller's own container is expected, and is safe when it is awaited; the hazard is simultaneity, not reuse.
    /// See ADR-0011 for the rationale.
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
        /// <exception cref="InvalidCastException">If a value is present but is not assignable to <typeparamref name="T"/></exception>
        /// <returns>The context of <typeparamref name="T"/> if found in the container</returns>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        public T Get<T>() 
            => Get<T>(typeof(T).FullName);

        /// <summary>
        /// Get context of <typeparamref name="T"/> from container.
        /// </summary>
        /// <typeparam name="T">The type of context to find in the container</typeparam>
        /// <exception cref="KeyNotFoundException">If no context of <typeparamref name="T"/> is found in the container</exception>
        /// <exception cref="InvalidCastException">If a value is present under <paramref name="fullQualifiedNamespaceOfType"/> but is not assignable to <typeparamref name="T"/></exception>
        /// <param name="fullQualifiedNamespaceOfType">The fully qualified type name of the context object to get from the container</param>
        /// <returns>The context of <typeparamref name="T"/> if found in the container</returns>
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>
        public T Get<T>(string fullQualifiedNamespaceOfType)
        {
            var outcome = FindTypedValue(fullQualifiedNamespaceOfType, out T result, out var storedValue);

            if (outcome == LookupOutcome.Missing)
            {
                throw new KeyNotFoundException("No item found in container with key: " + fullQualifiedNamespaceOfType);
            }

            if (outcome == LookupOutcome.TypeMismatch)
            {
                throw new InvalidCastException("Item found in container with key: " + fullQualifiedNamespaceOfType
                    + " is of type " + (storedValue?.GetType().FullName ?? "null")
                    + " which is not assignable to " + typeof(T).FullName);
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
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>.
        /// Found means a value is present and assignable to <typeparamref name="T"/>. A stored <see langword="null"/> is present when
        /// <typeparamref name="T"/> is a reference or nullable type, and is a mismatch when <typeparamref name="T"/> is a non-nullable
        /// value type. A present value which is not assignable to <typeparamref name="T"/> returns false with <paramref name="result"/>
        /// set to <see cref="default{T}"/>.
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
        /// If this context container was created with inherited context, the inherited context will also be searched for context of <typeparamref name="T"/>.
        /// Found means a value is present under <paramref name="fullQualifiedNamespaceOfType"/> and assignable to <typeparamref name="T"/>.
        /// A stored <see langword="null"/> is present when <typeparamref name="T"/> is a reference or nullable type, and is a mismatch when
        /// <typeparamref name="T"/> is a non-nullable value type. A present value which is not assignable to <typeparamref name="T"/> returns
        /// false with <paramref name="result"/> set to <see cref="default{T}"/>.
        public bool TryGet<T>(string fullQualifiedNamespaceOfType, out T result)
            => FindTypedValue(fullQualifiedNamespaceOfType, out result, out _) == LookupOutcome.Found;

        private enum LookupOutcome
        {
            Missing,
            Found,
            TypeMismatch
        }

        private LookupOutcome FindTypedValue<T>(string fullQualifiedNamespaceOfType, out T result, out object storedValue)
        {
            if (_context.TryGetValue(fullQualifiedNamespaceOfType, out var value))
            {
                storedValue = value;

                if (value is T typedValue)
                {
                    result = typedValue;
                    return LookupOutcome.Found;
                }

                result = default;
                // INVARIANT: a stored null is a present value for a reference or nullable T, but not for a
                // non-nullable value type T. See ADR-0011.
                return value is null && default(T) is null ? LookupOutcome.Found : LookupOutcome.TypeMismatch;
            }

            if (_inheritedContext != null)
            {
                return _inheritedContext.FindTypedValue(fullQualifiedNamespaceOfType, out result, out storedValue);
            }

            result = default;
            storedValue = null;
            return LookupOutcome.Missing;
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
        /// The value found by the same presence gate documented on <see cref="GetOrAdd{T}(Func{T})"/>'s <c>returns</c>, or
        /// <see cref="default{T}"/>, which is stored in the container before being returned.
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
        /// <paramref name="factoryMethod"/>. Presence is decided by the same gate as <see cref="TryGet{T}(out T)"/>:
        /// <paramref name="factoryMethod"/> runs whenever that gate reports no value for <typeparamref name="T"/>, which
        /// includes a value present under the key but not assignable to <typeparamref name="T"/> - that value is overwritten
        /// by the factory's result, which is stored even when it is <see langword="null"/> or a default value type.
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
        /// The non-<see langword="null"/> value found by the same presence gate documented on
        /// <see cref="GetOrAdd{T}(Func{T})"/>'s <c>returns</c>, otherwise a new instance of <typeparamref name="T"/> which is
        /// stored in the container before being returned. Unlike <see cref="GetOrAdd{T}(Func{T})"/>, a stored
        /// <see langword="null"/> is replaced with a new instance.
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
