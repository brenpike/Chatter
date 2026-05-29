# Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory token-based authentication for the Azure Service Bus broker.

## Language

**AAD Token Provider**: Supplies Azure Active Directory access tokens (scoped to `https://servicebus.azure.net/.default`) used to authenticate the Service Bus connection — `AzureActiveDirectoryTokenProvider`.
_Avoid_: credential provider.

**Token Provider Factory**: Builds an AAD Token Provider from configured options (`AadTokenProviderFactory`), via MSAL confidential-client.

**Credential Mode**: How the token is acquired — client-secret, client-certificate (X509 thumbprint), or interactive (`UseAadTokenProviderWithSecret` / `WithCert` / `Interactively`).

**Default Credential Fallback**: When no explicit credential is supplied, `DefaultAzureCredential` is used (managed identity, env, Azure CLI, etc.).

## Relationships

- Supplies credentials to the Azure Service Bus context's connection, applied only when the connection string carries no SAS token/key.
- Wired in via Service Bus Options builder extensions (a Credential Mode method).

## Example dialogue

> **Dev:** "Can I connect to Service Bus without a connection-string secret?"
> **Domain expert:** "Yes — register the AAD Token Provider; the Token Provider Factory issues Azure AD tokens for the connection instead of a shared key."

## Flagged ambiguities

None detected during bootstrap.
