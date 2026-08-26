# <a name="chatter-azureservicebus-auth"></a> Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory (AAD) token-based authentication for the Chatter Azure Service Bus broker.

## Overview

`Chatter.MessageBrokers.AzureServiceBus.Auth` lets the [Chatter.MessageBrokers.AzureServiceBus](../../../README.md#chatter-azureservicebus) broker authenticate to Azure Service Bus with **Azure Active Directory access tokens** instead of a connection-string shared-access key (SAS).

Instead of embedding a `SharedAccessKey`/`SharedAccessSignature` in the connection string, you supply a service-principal client ID (and a secret, certificate, or interactive redirect URI), or ask for an Azure managed identity outright. The package builds an `Azure.Core.TokenCredential` (e.g. a `ClientSecretCredential`, `ClientCertificateCredential`, `InteractiveBrowserCredential`, or `ManagedIdentityCredential`) and hands it to the Service Bus connection. When no explicit credential is supplied, it falls back to `DefaultAzureCredential` (managed identity, environment, Azure CLI, etc.), so the same code works locally and in Azure-hosted environments.

The package extends the `ServiceBusOptionsBuilder` exposed by the base broker — you opt in by calling one of the `UseAadTokenProvider*` extension methods. The base broker only applies the token provider when the configured connection string does **not** already contain a SAS token or key, so AAD auth is purely additive.

## Installation

```sh
dotnet add package Chatter.MessageBrokers.AzureServiceBus.Auth
```

## Getting Started

Enable AAD auth inside the `AddAzureServiceBus` options builder. Supply a connection string **without** a shared-access key (just the `Endpoint`), then call one of the `UseAadTokenProvider*` extensions:

```csharp
using Chatter.MessageBrokers;
using Microsoft.Extensions.DependencyInjection;

services.AddChatter()
        .AddAzureServiceBus(sb =>
        {
            // Endpoint only — no SharedAccessKey/SharedAccessSignature
            sb.WithConnectionString("Endpoint=sb://my-namespace.servicebus.windows.net/");

            // Authenticate with an AAD app registration + client secret
            sb.UseAadTokenProviderWithSecret(
                clientId:     "00000000-0000-0000-0000-000000000000",
                clientSecret: "<client-secret>",
                authority:    "https://login.microsoftonline.com/<tenant-id>/");
        });
```

Four extension methods are available on `ServiceBusOptionsBuilder`:

- **`UseAadTokenProviderWithSecret(clientId, clientSecret, authority, optBuilder = null)`** — authenticate as a confidential client using a client secret.
- **`UseAadTokenProviderWithCert(clientId, thumbPrint, authority, optBuilder = null, validCertsOnly = true)`** — authenticate using a client certificate located by thumbprint in the `CurrentUser`/`LocalMachine` `My` store. Set `validCertsOnly: false` for self-signed certs.
- **`UseAadTokenProviderInteractively(clientId, redirectUri, optBuilder = null)`** — authenticate via an interactive flow using the supplied redirect URI.
- **`UseAadTokenProviderWithManagedIdentity(clientId = null, optBuilder = null)`** — authenticate as an Azure managed identity. Pass the client ID of a user-assigned managed identity, or omit it (or pass null/whitespace) for the system-assigned identity.

### Managed identity

To authenticate as an Azure managed identity, use the managed-identity mode:

```csharp
// User-assigned managed identity
sb.UseAadTokenProviderWithManagedIdentity(clientId: "<user-assigned-mi-client-id>");

// System-assigned managed identity — omit the client id
sb.UseAadTokenProviderWithManagedIdentity();
```

This mode returns a `ManagedIdentityCredential` whose identity **is** the client ID you passed. It never consults the `DefaultAzureCredential` chain, so no other credential source — an environment-backed service principal, a developer's `az login` session — can answer in the managed identity's place. `AZURE_TOKEN_CREDENTIALS` and the `Exclude*` credential flags have no effect here, because there is no chain for them to narrow.

A null or whitespace `clientId` asks for the **system-assigned** managed identity. One caveat: on a federated-token host (for example AKS workload identity), the Azure SDK's token-exchange managed-identity source falls back to the `AZURE_CLIENT_ID` environment variable when no identity was supplied, so the blank case authenticates as the platform-bound workload identity rather than literally system-assigned. Passing a client ID explicitly avoids that fallback entirely.

**This credential fails loudly off Azure.** With no managed-identity endpoint reachable, token acquisition raises `CredentialUnavailableException` instead of quietly falling through to another credential — and it may take a noticeable moment to surface, since the SDK's IMDS probe timeout for this path is internal and not caller-tunable. That is the intended trade: on the previous chained behavior a developer machine silently authenticated as **the developer** rather than as the managed identity, and the failure only appeared once something depended on the managed identity's role assignments. To keep one code path across environments, switch modes by environment — the managed-identity mode in Azure, and one of the `DefaultAzureCredential` fallbacks below locally, which still honor `az login`:

```csharp
if (env.IsDevelopment())
{
    // Blank redirect URI falls back to DefaultAzureCredential, which picks up `az login`
    sb.UseAadTokenProviderInteractively(clientId: "<app-client-id>", redirectUri: null);
}
else
{
    sb.UseAadTokenProviderWithManagedIdentity(clientId: "<user-assigned-mi-client-id>");
}
```

> **Named argument required for `optBuilder`-only calls.** `optBuilder` configures `ManagedIdentityCredentialOptions`, whose most useful settable member is the inherited `AuthorityHost` (for sovereign clouds); it cannot redirect the identity, because the SDK's `ManagedIdentityId` property is internal and get-only. The first parameter is `string clientId`, so a bare lambda in the first positional slot is a compile error. Write it as:
>
> ```csharp
> sb.UseAadTokenProviderWithManagedIdentity(
>     optBuilder: opts => opts.AuthorityHost = new Uri("https://login.microsoftonline.us/"));
> ```

### Default credential fallback

If the credential argument (`clientSecret` / `thumbPrint` / `redirectUri`) is null or whitespace, the secret, certificate, and interactive modes fall back to `DefaultAzureCredential` and resolve the identity **ambiently** from host state. This is the package's only ambient path, and the one that honors `az login` — which makes it the local-development counterpart to the managed-identity mode above. Use the optional `optBuilder` delegate to configure `DefaultAzureCredentialOptions` for that fallback:

```csharp
sb.UseAadTokenProviderInteractively(
    clientId:    "00000000-0000-0000-0000-000000000000",
    redirectUri: null,                        // fall back to DefaultAzureCredential
    optBuilder:  opts => opts.ExcludeAzureCliCredential = true);
```

## How It Works

- **AAD Token Credential** — every extension method ultimately produces an `Azure.Core.TokenCredential`. This is registered on the builder via `ServiceBusOptionsBuilder.AddTokenProvider(Func<TokenCredential>)`. The factory delegate is invoked **eagerly** at registration time — `AddTokenProvider` calls `tokenCredentialFactory?.Invoke()` immediately rather than deferring construction until the options are built. A practical consequence: when using `UseAadTokenProviderWithCert`, the X509 certificate store is read at registration time, not at first token acquisition.
- **`AadTokenProviderFactory`** — created with `AadTokenProviderFactory.Create(clientId)`, the factory exposes `WithSecret(...)`, `WithCert(...)`, `WithInteractive(...)`, and `WithManagedIdentity(...)`, each returning a `TokenCredential` (`ClientSecretCredential`, `ClientCertificateCredential`, `InteractiveBrowserCredential`, or `DefaultAzureCredential` on fallback). `WithManagedIdentity(...)` is the only method that returns none of those — it constructs a `ManagedIdentityCredential` directly, so it is also the only method that never involves the `DefaultAzureCredential` chain.
- **Token acquisition** — for explicit credentials the factory constructs the corresponding `Azure.Identity` credential type directly. `WithSecret(...)` and `WithCert(...)` parse the tenant ID and authority host out of the supplied `authority` URL (the tenant ID is the first non-empty path segment and any deeper segments are ignored, so both a bare directory URL of the form `https://login.microsoftonline.com/<tenant-id>/` and the `/v2.0`-suffixed issuer URL the Azure portal hands out are accepted; the scheme+host becomes `AuthorityHost`) and build a `ClientSecretCredential` or `ClientCertificateCredential`; for the certificate path the cert is resolved from the X509 `My` store (`CurrentUser`, then `LocalMachine`) by thumbprint, throwing if not found. Because the first segment is taken as-is, any authority whose first segment is not the tenant ID — a non-AAD routing prefix such as Azure AD B2C's `/tfp/<tenant>/<policy>`, ADFS, or DSTS, or a malformed AAD URL that omits the tenant (e.g. a pasted `/oauth2/v2.0/token` endpoint) — resolves to a well-formed but wrong tenant ID, so the credential constructs and the misconfiguration surfaces at first token acquisition rather than at credential construction. A wrong tenant ID changes only which directory is asked, never which principal asks: the client ID and the secret or certificate are the ones you supplied. `WithInteractive(...)` takes no `authority` — it builds an `InteractiveBrowserCredential` from `InteractiveBrowserCredentialOptions` carrying only `ClientId` and `RedirectUri`, so the interactive path is not tenant-scoped by an authority value. When no explicit credential is provided, it returns a `DefaultAzureCredential`. `WithManagedIdentity(...)` takes no `authority` either — it resolves the factory's client ID to a `ManagedIdentityId` (`FromUserAssignedClientId`, or `SystemAssigned` when the client ID is null or whitespace), hands that to `ManagedIdentityCredentialOptions`, invokes the caller's `optBuilder`, and constructs a `ManagedIdentityCredential`. The identity travels on the credential itself rather than being selected from a chain, so `optBuilder` can adjust transport settings such as `AuthorityHost` but cannot change which identity is requested.
- **Connection wiring** — during `ServiceBusOptionsBuilder.Build()`, the token credential is assigned to the Service Bus options **only if** the connection string has no SAS token/key. The Service Bus connection then uses the issued AAD bearer token in place of a shared key.

## Domain Language

See the module domain glossary: [`../CONTEXT.md`](../CONTEXT.md).

[← All Chatter modules](../../../README.md)
