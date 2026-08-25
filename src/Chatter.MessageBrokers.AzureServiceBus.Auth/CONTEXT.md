# Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory token-based authentication for the Azure Service Bus broker.

## Language

**AAD Token Provider**: Supplies Azure Active Directory access tokens (scoped to `https://servicebus.azure.net/.default`) used to authenticate the Service Bus connection — an `Azure.Core.TokenCredential` produced by the Token Provider Factory.
_Avoid_: credential provider.

**Token Provider Factory**: Builds an AAD Token Provider from configured options (`AadTokenProviderFactory`), via `Azure.Identity` — returns a `ClientSecretCredential`, `ClientCertificateCredential`, or `InteractiveBrowserCredential` depending on Credential Mode, falling back to `DefaultAzureCredential` when the distinguishing credential value is not supplied. `WithManagedIdentity(...)` is the one factory method that produces a `DefaultAzureCredential` deliberately rather than as a fallback, pinning the supplied client id onto the credential's options.

**Credential Mode**: How the token is acquired — client-secret, client-certificate (X509 thumbprint), interactive, or managed-identity (`UseAadTokenProviderWithSecret` / `UseAadTokenProviderWithCert` / `UseAadTokenProviderInteractively` / `UseAadTokenProviderWithManagedIdentity`). The managed-identity mode selects the *identity*, not a distinct credential type: it returns a `DefaultAzureCredential` whose `ManagedIdentityClientId` and `WorkloadIdentityClientId` are both pinned to the supplied client id (before the caller's `optBuilder` runs). Both are pinned because `WorkloadIdentityClientId` also defaults to the `AZURE_CLIENT_ID` environment variable and `WorkloadIdentityCredential` precedes `ManagedIdentityCredential` in the chain, so pinning only the managed-identity arm would let a workload-identity host authenticate as the ambient identity instead of the requested one. The full `DefaultAzureCredential` chain still applies and an ambient `AZURE_TOKEN_CREDENTIALS` can still narrow it. A blank or whitespace client id pins nothing and leaves both properties at their defaults — that is how a system-assigned managed identity is requested.
_Avoid_: managed-identity credential — the mode does not produce a `ManagedIdentityCredential`.

**Default Credential Fallback**: The *implicit* path — when the distinguishing credential value of the client-secret, client-certificate, or interactive Credential Mode is blank, that mode returns a `DefaultAzureCredential` (managed identity, env, Azure CLI, etc.) with **no client id pinned**, so the identity is resolved ambiently. Distinct from the *explicit* managed-identity Credential Mode, which also yields a `DefaultAzureCredential` but pins the supplied client id onto it. The pin belongs to the managed-identity mode alone; the fallbacks are unchanged by it.

## Relationships

- Supplies credentials to the Azure Service Bus context's connection, applied only when the connection string carries no SAS token/key.
- Wired in via Service Bus Options builder extensions (a Credential Mode method).

## Example dialogue

> **Dev:** "Can I connect to Service Bus without a connection-string secret?"
> **Domain expert:** "Yes — register the AAD Token Provider; the Token Provider Factory issues Azure AD tokens for the connection instead of a shared key."

## Flagged ambiguities

None detected during bootstrap.
