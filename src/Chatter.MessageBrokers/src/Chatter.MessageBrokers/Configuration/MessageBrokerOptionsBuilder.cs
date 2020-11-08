using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private readonly IConfiguration _configuration;
        private MessageBrokerOptions _messageBrokerOptions;
        private ReliabilityOptions _reliabilityOptions;
        private RecoveryOptions _recoveryOptions;
        private Func<IServiceCollection> _optionConfigurator;
        private const string _messageBrokerSectionName = "Chatter:MessageBrokers";
        private const string _reliabilityOptionsSectionName = _messageBrokerSectionName + ":Reliability";
        private const string _recoveryOptionsSectionName = _messageBrokerSectionName + ":Recovery";

        internal MessageBrokerOptionsBuilder(IServiceCollection services, IConfiguration configuration)
        {
            Services = services;
            _configuration = configuration;
            _messageBrokerOptions = new MessageBrokerOptions();
            _optionConfigurator = () => Services.Configure<MessageBrokerOptions>(GetMessageBrokerOptions(_messageBrokerSectionName));
        }

        private IConfigurationSection GetMessageBrokerOptions(string messageBrokerSectionName)
        {
            var messageBrokerOptionsSection = _configuration.GetSection(messageBrokerSectionName);
            _messageBrokerOptions = messageBrokerOptionsSection.Get<MessageBrokerOptions>();
            return messageBrokerOptionsSection;
        }

        public MessageBrokerOptionsBuilder AddMessageBrokerOptions(Action<MessageBrokerOptions> builder)
        {
            _optionConfigurator = () => Services.Configure(builder);
            return this;
        }

        public MessageBrokerOptionsBuilder AddMessageBrokerOptions(string messageBrokerSectionName = _messageBrokerSectionName)
        {
            _optionConfigurator = () => Services.Configure<MessageBrokerOptions>(GetMessageBrokerOptions(messageBrokerSectionName));
            return this;
        }

        public MessageBrokerOptionsBuilder AddReliabilityOptions(string reliabilityOptionsSectionName = _reliabilityOptionsSectionName)
        {
            var reliabilityOptions = _configuration.GetSection(reliabilityOptionsSectionName).Get<ReliabilityOptions>();

            if (reliabilityOptions is null)
            {
                throw new ArgumentNullException(nameof(reliabilityOptions), $"No reliability options found in section '{reliabilityOptionsSectionName}'");
            }

            _reliabilityOptions = reliabilityOptions;

            return this;
        }

        public MessageBrokerOptionsBuilder AddRecoveryOptions(string recoveryOptionsSectionName = _recoveryOptionsSectionName)
        {
            var recoveryOptions = _configuration.GetSection(recoveryOptionsSectionName).Get<RecoveryOptions>();

            if (recoveryOptions is null)
            {
                throw new ArgumentNullException(nameof(recoveryOptions), $"No recovery options found in section '{recoveryOptionsSectionName}'");
            }

            _recoveryOptions = recoveryOptions;

            return this;
        }

        private void PostConfiguration(MessageBrokerOptions messageBrokerOptions)
        {
            if (_reliabilityOptions != null)
            {
                messageBrokerOptions.Reliability = _reliabilityOptions;
            }

            if (_recoveryOptions != null)
            {
                messageBrokerOptions.Recovery = _recoveryOptions;
            }

            _messageBrokerOptions = messageBrokerOptions;
        }

        internal MessageBrokerOptions Build()
        {
            Services.AddOptions<MessageBrokerOptions>()
                     .ValidateDataAnnotations()
                     .PostConfigure(options =>
                     {
                         PostConfiguration(options);
                     });

            _optionConfigurator?.Invoke();

            Services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value);
            Services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value?.Reliability ?? new ReliabilityOptions());
            Services.AddSingleton(resolver => resolver.GetRequiredService<IOptions<MessageBrokerOptions>>().Value?.Recovery ?? new RecoveryOptions());

            PostConfiguration(_messageBrokerOptions);
            return _messageBrokerOptions;
        }
    }
}
