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

        public ContextContainer(ContextContainer inheritedContext = null)
        {
            _inheritedContext = inheritedContext;
        }

        public T Get<T>()
        {
            return Get<T>(typeof(T).FullName);
        }

        public bool TryGet<T>(out T result)
        {
            return TryGet(typeof(T).FullName, out result);
        }

        public bool TryGet<T>(string key, out T result)
        {
            if (_context.TryGetValue(key, out var value))
            {
                result = (T)value;
                return true;
            }

            if (_inheritedContext != null)
            {
                return _inheritedContext.TryGet(key, out result);
            }

            result = default;
            return false;
        }

        public T Get<T>(string key)
        {
            if (!TryGet(key, out T result))
            {
                throw new KeyNotFoundException("No item found in context with key: " + key);
            }

            return result;
        }

        public T GetOrCreate<T>() where T : class, new()
        {
            if (TryGet(out T value))
            {
                return value;
            }

            var newInstance = new T();

            Set(newInstance);

            return newInstance;
        }

        public void Set<T>(T t)
        {
            Set(typeof(T).FullName, t);
        }

        public void Remove<T>()
        {
            Remove(typeof(T).FullName);
        }

        public void Remove(string key)
        {
            _context.Remove(key);
        }

        public void Set<T>(string key, T t)
        {
            _context[key] = t;
        }
    }
}
