using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Resolves the <see cref="IBrokeredMessageBodyConverter"/> registered for a content type, falling back to a
    /// <see cref="JsonBodyConverter"/> shared by every lookup this factory cannot answer.
    /// </summary>
    /// <remarks>
    /// <c>AddMessageBrokers</c> registers this factory and the <see cref="IBrokeredMessageBodyConverter"/> instances it
    /// enumerates as singletons, so they live for the lifetime of the process. Register custom converters as
    /// singletons too: a converter registered with a shorter lifetime is captured by this factory and, when scope
    /// validation is enabled, resolving the factory throws <see cref="System.InvalidOperationException"/>.
    /// </remarks>
    public class BodyConverterFactory : IBodyConverterFactory
    {
        private readonly ConcurrentDictionary<string, IBrokeredMessageBodyConverter> _bodyConverterProviders = new ConcurrentDictionary<string, IBrokeredMessageBodyConverter>();
        private readonly JsonBodyConverter _fallbackBodyConverter = new JsonBodyConverter();

        public BodyConverterFactory(IEnumerable<IBrokeredMessageBodyConverter> bodyConverterProviders)
        {
            InitProviderLookup(bodyConverterProviders);
        }

        private void InitProviderLookup(IEnumerable<IBrokeredMessageBodyConverter> bodyConverterProviders)
        {
            foreach (var converter in bodyConverterProviders)
            {
                _bodyConverterProviders[converter.ContentType] = converter;
            }
        }

        /// <summary>
        /// Gets the <see cref="IBrokeredMessageBodyConverter"/> registered for <paramref name="contentType"/>.
        /// </summary>
        /// <param name="contentType">The content type of the brokered message body. A null, empty, or whitespace content type is treated as unknown.</param>
        /// <returns>The <see cref="IBrokeredMessageBodyConverter"/> registered for <paramref name="contentType"/>, or this factory's shared fallback <see cref="JsonBodyConverter"/> when the content type is unknown. The fallback is never a caller-registered converter.</returns>
        public IBrokeredMessageBodyConverter CreateBodyConverter(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType) || !(_bodyConverterProviders.TryGetValue(contentType, out var converter)))
            {
                return _fallbackBodyConverter;
            }

            return converter;
        }
    }
}
