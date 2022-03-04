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

        /// <summary>
        /// 
        /// </summary>
        /// <param name="contentType"></param>
        /// <returns>The appropriate <see cref="IBrokeredMessageBodyConverter"/> for specified <paramref name="contentType"/> or <see cref="JsonBodyConverter"/> if no <see cref="IBrokeredMessageBodyConverter"/> was found.</returns>
        public IBrokeredMessageBodyConverter CreateBodyConverter(string contentType)
        {
            if (!(_bodyConverterProviders.TryGetValue(contentType, out var converter)))
            {
                return new JsonBodyConverter();
            }

            return converter;
        }
    }
}
