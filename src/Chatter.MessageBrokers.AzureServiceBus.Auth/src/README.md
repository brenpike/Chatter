# <a name="chatter-azureservicebus-auth"></a> Chatter.MessageBrokers.AzureServiceBus.Auth

Azure Active Directory (AAD) token-based authentication for the Chatter Azure Service Bus broker.

## Overview

`Chatter.MessageBrokers.AzureServiceBus.Auth` lets the [Chatter.MessageBrokers.AzureServiceBus](../../../README.md#chatter-azureservicebus) broker authenticate to Azure Service Bus with **Azure Active Directory access tokens** instead of a connection-string shared-access key (SAS).

Instead of embedding a `SharedAccessKey`/`SharedAccessSignature` in the connection string, you supply a service-principal client ID (and a secret, certificate, or interactive redirect URI). The package builds an `AzureActiveDirectoryTokenProvider` that acquires bearer tokens scoped to `https://servicebus.azure.net/.default` and hands them to the Service Bus connection. When no explicit credential is supplied, it falls back to `DefaultAzureCredential` (managed identity, environment, Azure CLI, etc.), so the same code works locally and in Azure-hosted environments.

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

Three extension methods are available on `ServiceBusOptionsBuilder`:

- **`UseAadTokenProviderWithSecret(clientId, clientSecret, authority, optBuilder = null)`** — authenticate as a confidential client using a client secret.
- **`UseAadTokenProviderWithCert(clientId, thumbPrint, authority, optBuilder = null, validCertsOnly = true)`** — authenticate using a client certificate located by thumbprint in the `CurrentUser`/`LocalMachine` `My` store. Set `validCertsOnly: false` for self-signed certs.
- **`UseAadTokenProviderInteractively(clientId, redirectUri, optBuilder = null)`** — authenticate via an interactive flow using the supplied redirect URI.

If the credential argument (`clientSecret` / `thumbPrint` / `redirectUri`) is null or whitespace, the provider falls back to `DefaultAzureCredential`. Use the optional `optBuilder` delegate to configure `DefaultAzureCredentialOptions` for that fallback:

```csharp
sb.UseAadTokenProviderWithSecret(
    clientId:     "00000000-0000-0000-0000-000000000000",
    clientSecret: null,                       // fall back to DefaultAzureCredential
    authority:    "https://login.microsoftonline.com/<tenant-id>/",
    optBuilder:   opts => opts.ManagedIdentityClientId = "<user-assigned-mi-client-id>");
```

## How It Works

- **AAD Token Provider** — every extension method ultimately produces an `AzureActiveDirectoryTokenProvider` (from `Microsoft.Azure.ServiceBus.Primitives`). This is registered on the builder via `ServiceBusOptionsBuilder.AddTokenProvider(Func<ITokenProvider>)`, deferring construction until the options are built.
- **`AadTokenProviderFactory`** — created with `AadTokenProviderFactory.Create(clientId)`, the factory exposes `WithSecret(...)`, `WithCert(...)`, and `WithInteractive(...)`, each returning a configured `AzureActiveDirectoryTokenProvider`. The factory pins the token request scope to `https://servicebus.azure.net/.default`.
- **Token acquisition** — for explicit credentials the factory uses MSAL's `ConfidentialClientApplicationBuilder` (`AcquireTokenForClient`) to obtain the access token from the configured `authority`. With a certificate, it is resolved from the X509 store by thumbprint (throwing if not found). When no explicit credential is provided, it calls `DefaultAzureCredential.GetTokenAsync(...)` for the same scope.
- **Connection wiring** — during `ServiceBusOptionsBuilder.Build()`, the token provider is assigned to the Service Bus options **only if** the connection string has no `SasToken` and no `SasKey`. The Service Bus connection then uses the issued AAD bearer token in place of a shared key.

## Domain Language

See the module domain glossary: [`../CONTEXT.md`](../CONTEXT.md).

[← All Chatter modules](../../../README.md)
