using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers
{
    public class BodyConverterFactory : IBodyConverterFactory
    {
        private readonly ConcurrentDictionary<string, IBrokeredMessageBodyConverter> _bodyConverterProviders = new ConcurrentDictionary<string, IBrokeredMessageBodyConverter>();

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

        public IBrokeredMessageBodyConverter CreateBodyConverter(string contentType)
        {
            if (!(_bodyConverterProviders.TryGetValue(contentType, out var converter)))
            {
                throw new KeyNotFoundException($"No {typeof(IBrokeredMessageBodyConverter).Name} was found for content type '{contentType}'.");
            }

            return converter;
        }
    }
}
