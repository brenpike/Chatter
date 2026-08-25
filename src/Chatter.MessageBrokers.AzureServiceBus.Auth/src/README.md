# <a name="chatter-azureservicebus-auth"></a> Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory (AAD) token-based authentication for the Chatter Azure Service Bus broker.

## Overview

`Chatter.MessageBrokers.AzureServiceBus.Auth` lets the [Chatter.MessageBrokers.AzureServiceBus](../../../README.md#chatter-azureservicebus) broker authenticate to Azure Service Bus with **Azure Active Directory access tokens** instead of a connection-string shared-access key (SAS).

Instead of embedding a `SharedAccessKey`/`SharedAccessSignature` in the connection string, you supply a service-principal client ID (and a secret, certificate, or interactive redirect URI), or ask for an Azure managed identity outright. The package builds an `Azure.Core.TokenCredential` (e.g. a `ClientSecretCredential`, `ClientCertificateCredential`, or `InteractiveBrowserCredential`) and hands it to the Service Bus connection. When no explicit credential is supplied, it falls back to `DefaultAzureCredential` (managed identity, environment, Azure CLI, etc.), so the same code works locally and in Azure-hosted environments.

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

This mode selects the **identity**, not a distinct credential type. It returns a `DefaultAzureCredential` with the supplied client ID pinned onto **both** `ManagedIdentityClientId` and `WorkloadIdentityClientId` before your `optBuilder` runs — it does *not* return a `ManagedIdentityCredential`. The full `DefaultAzureCredential` chain still applies, and an ambient `AZURE_TOKEN_CREDENTIALS` environment variable can still narrow it.

Both properties are pinned because `WorkloadIdentityClientId` also defaults to the `AZURE_CLIENT_ID` environment variable, and `WorkloadIdentityCredential` comes before `ManagedIdentityCredential` in the chain. Pinning only the managed-identity arm would let an AKS workload-identity host silently authenticate as the ambient identity instead of the one you passed.

A null or whitespace `clientId` pins nothing and leaves both properties at their `DefaultAzureCredentialOptions` defaults, which is how you ask for the system-assigned managed identity.

> **Named argument required for `optBuilder`-only calls.** The first parameter is `string clientId`, so a bare lambda in the first positional slot is a compile error. Write it as:
>
> ```csharp
> sb.UseAadTokenProviderWithManagedIdentity(optBuilder: opts => opts.Retry.MaxRetries = 5);
> ```

### Default credential fallback

If the credential argument (`clientSecret` / `thumbPrint` / `redirectUri`) is null or whitespace, the secret, certificate, and interactive modes fall back to `DefaultAzureCredential` and resolve the identity **ambiently** — no client ID is pinned on that path. Use the optional `optBuilder` delegate to configure `DefaultAzureCredentialOptions` for that fallback:

```csharp
sb.UseAadTokenProviderInteractively(
    clientId:    "00000000-0000-0000-0000-000000000000",
    redirectUri: null,                        // fall back to DefaultAzureCredential
    optBuilder:  opts => opts.ExcludeAzureCliCredential = true);
```

## How It Works

- **AAD Token Credential** — every extension method ultimately produces an `Azure.Core.TokenCredential`. This is registered on the builder via `ServiceBusOptionsBuilder.AddTokenProvider(Func<TokenCredential>)`. The factory delegate is invoked **eagerly** at registration time — `AddTokenProvider` calls `tokenCredentialFactory?.Invoke()` immediately rather than deferring construction until the options are built. A practical consequence: when using `UseAadTokenProviderWithCert`, the X509 certificate store is read at registration time, not at first token acquisition.
- **`AadTokenProviderFactory`** — created with `AadTokenProviderFactory.Create(clientId)`, the factory exposes `WithSecret(...)`, `WithCert(...)`, `WithInteractive(...)`, and `WithManagedIdentity(...)`, each returning a `TokenCredential` (`ClientSecretCredential`, `ClientCertificateCredential`, `InteractiveBrowserCredential`, or `DefaultAzureCredential` on fallback). `WithManagedIdentity(...)` is the only method that returns a `DefaultAzureCredential` deliberately rather than as a fallback, and the only one that pins a client ID onto it.
- **Token acquisition** — for explicit credentials the factory constructs the corresponding `Azure.Identity` credential type directly. `WithSecret(...)` and `WithCert(...)` parse the tenant ID and authority host out of the supplied `authority` URL (the tenant ID is taken from the URL path, so supply a directory URL of the form `https://login.microsoftonline.com/<tenant-id>/` and no deeper path; the scheme+host becomes `AuthorityHost`) and build a `ClientSecretCredential` or `ClientCertificateCredential`; for the certificate path the cert is resolved from the X509 `My` store (`CurrentUser`, then `LocalMachine`) by thumbprint, throwing if not found. `WithInteractive(...)` takes no `authority` — it builds an `InteractiveBrowserCredential` from `InteractiveBrowserCredentialOptions` carrying only `ClientId` and `RedirectUri`, so the interactive path is not tenant-scoped by an authority value. When no explicit credential is provided, it returns a `DefaultAzureCredential`. `WithManagedIdentity(...)` takes no `authority` either — it builds a `DefaultAzureCredential` from `DefaultAzureCredentialOptions` whose `ManagedIdentityClientId` and `WorkloadIdentityClientId` are set to the factory's client ID (skipped when that client ID is null or whitespace) before the caller's `optBuilder` is invoked, so the caller can still override the pin.
- **Connection wiring** — during `ServiceBusOptionsBuilder.Build()`, the token credential is assigned to the Service Bus options **only if** the connection string has no SAS token/key. The Service Bus connection then uses the issued AAD bearer token in place of a shared key.

## Domain Language

See the module domain glossary: [`../CONTEXT.md`](../CONTEXT.md).

[← All Chatter modules](../../../README.md)
