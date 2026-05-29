# Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory token-based authentication for the Azure Service Bus broker.

## Language

**AAD Token Provider**: Supplies Azure Active Directory access tokens used to authenticate the Service Bus connection.
_Avoid_: credential provider.

**Token Provider Factory**: Builds an AAD Token Provider from configured options.

## Relationships

- Supplies credentials to the Azure Service Bus context's connection.
- Wired in via Service Bus Options builder extensions.

## Example dialogue

> **Dev:** "Can I connect to Service Bus without a connection-string secret?"
> **Domain expert:** "Yes — register the AAD Token Provider; the Token Provider Factory issues Azure AD tokens for the connection instead of a shared key."

## Flagged ambiguities

None detected during bootstrap.
