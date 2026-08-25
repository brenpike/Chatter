# Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory token-based authentication for the Azure Service Bus broker.

## Language

**AAD Token Provider**: Supplies Azure Active Directory access tokens (scoped to `https://servicebus.azure.net/.default`) used to authenticate the Service Bus connection — an `Azure.Core.TokenCredential` produced by the Token Provider Factory.
_Avoid_: credential provider.

**Token Provider Factory**: Builds an AAD Token Provider from configured options (`AadTokenProviderFactory`), via `Azure.Identity` — returns a `ClientSecretCredential`, `ClientCertificateCredential`, or `InteractiveBrowserCredential` depending on Credential Mode, falling back to `DefaultAzureCredential` when the distinguishing credential value is not supplied. `WithManagedIdentity(...)` is the one factory method that returns neither a distinguishing-value credential nor a `DefaultAzureCredential`: it constructs a `ManagedIdentityCredential` directly from the supplied client id.

**Credential Mode**: How the token is acquired — client-secret, client-certificate (X509 thumbprint), interactive, or managed-identity (`UseAadTokenProviderWithSecret` / `UseAadTokenProviderWithCert` / `UseAadTokenProviderInteractively` / `UseAadTokenProviderWithManagedIdentity`). The managed-identity mode produces a `ManagedIdentityCredential` whose identity *is* the supplied client id (`ManagedIdentityId.FromUserAssignedClientId`); a blank or whitespace client id means `ManagedIdentityId.SystemAssigned`. Because the mode builds no credential chain, no ambient credential source can answer in the managed identity's place, and `AZURE_TOKEN_CREDENTIALS` and the `Exclude*` flags have no effect on it. The caller's `optBuilder` configures `ManagedIdentityCredentialOptions` but cannot redirect the identity — the SDK's `ManagedIdentityId` property is internal and get-only. **Accepted residue**: with a supplied client id the requested identity is total, but the blank/system-assigned case on a federated-token host (AKS workload identity) falls back to the `AZURE_CLIENT_ID` environment variable inside the SDK's token-exchange source, so it authenticates as the platform-bound workload identity rather than literally system-assigned. That source's exclusion switch is internal and excluding it would break AKS, so the residue is bounded, not closed.
_Avoid_: pinned client id / both arms pinned / pinned `DefaultAzureCredential` — the mode no longer selects an identity within a chain; it names the identity on the credential itself.

**Directory Authority**: The directory URL supplied to the client-secret and client-certificate Credential Modes (`authority`) — the first non-empty path segment is the tenant id, deeper segments are ignored, and the scheme+host becomes the credential's `AuthorityHost`. Both a bare `https://login.microsoftonline.com/{tenant}/` and the `/v2.0`-suffixed issuer URL Microsoft publishes are therefore accepted. A blank or non-absolute value yields no tenant id at all, and `Azure.Identity` then rejects the credential construction with `ArgumentNullException`. **Accepted residue**: taking the first segment as-is means any authority whose first segment is not the tenant id — a non-AAD routing prefix (Azure AD B2C's `/tfp/{tenant}/{policy}`, ADFS, DSTS) or a malformed AAD URL omitting the tenant (a pasted `/oauth2/v2.0/token` endpoint) — resolves to a character-set-valid but wrong tenant id, so the credential constructs and fails loudly at first token acquisition instead of at construction. Bounded, not closed: the misparsed segment names no real directory so no token is issued, non-AAD authority types cannot authenticate to Azure Service Bus at all, and pre-3.0.0 MSAL behaved identically — no working configuration is lost.
_Avoid_: tenant-only authority / "no deeper path" — deeper segments are ignored, not rejected.

**Default Credential Fallback**: The *implicit* path, and the only ambient-resolution path in the package — when the distinguishing credential value of the client-secret, client-certificate, or interactive Credential Mode is blank, that mode returns a `DefaultAzureCredential` (managed identity, env, Azure CLI, etc.) and the identity is resolved ambiently from host state. This is also the documented home for local-development ergonomics: the fallback still honors `az login`. Distinct from the *explicit* managed-identity Credential Mode, which resolves nothing ambiently and never reaches this chain.

## Relationships

- Supplies credentials to the Azure Service Bus context's connection, applied only when the connection string carries no SAS token/key.
- Wired in via Service Bus Options builder extensions (a Credential Mode method).

## Example dialogue

> **Dev:** "Can I connect to Service Bus without a connection-string secret?"
> **Domain expert:** "Yes — register the AAD Token Provider; the Token Provider Factory issues Azure AD tokens for the connection instead of a shared key."

## Flagged ambiguities

None detected during bootstrap.
