# Chatter.MessageBrokers.AzureServiceBus.Auth

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.AzureServiceBus.Auth.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.AzureServiceBus.Auth.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**Azure AD (Microsoft Entra ID) token authentication for the Azure Service Bus transport: client secret, certificate, interactive and managed identity.**

This package lets [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus) connect with a Microsoft Entra ID token instead of a shared access key. You add one `UseAadTokenProvider*` call to the `AddAzureServiceBus` builder and give it an endpoint-only connection string. The package builds an `Azure.Identity` credential for you. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Credential modes](#credential-modes)
- [Local development](#local-development)
- [Configuration](#configuration)
- [How it works](#how-it-works)
- [Troubleshooting](#troubleshooting)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Four credential modes**: client secret, client certificate, interactive browser sign-in and managed identity, each one builder call.
- **System-assigned and user-assigned managed identity**: omit the client id for system-assigned, or pass it for user-assigned.
- **Exact identity for managed identity**: the managed identity mode builds a `ManagedIdentityCredential` directly, so no other credential on the host can answer in its place.
- **Default credential fallback**: leave the secret, thumbprint or redirect URI blank and you get a `DefaultAzureCredential`, which honors `az login`.
- **Certificate store lookup**: certificates are found by thumbprint in the `My` store, `CurrentUser` first, then `LocalMachine`.
- **Sovereign clouds**: the authority host comes from the `authority` URL, or from `AuthorityHost` for managed identity.
- **SAS still wins**: the token is used only when the connection string holds no shared access key or signature.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.AzureServiceBus.Auth
```

Targets .NET 10 (`net10.0`).

Dependencies: `Chatter.MessageBrokers.AzureServiceBus`, `Azure.Identity` 1.21.0.

The extension methods are in the `Microsoft.Extensions.DependencyInjection` namespace, so no extra `using` line is needed.

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way. For messages, handlers and receivers, see the [Azure Service Bus quick start](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#quick-start).

### 1. Use an endpoint-only connection string

Leave out `SharedAccessKeyName`, `SharedAccessKey` and `SharedAccessSignature`. The client connects to the fully qualified namespace taken from `Endpoint`.

```text
Endpoint=sb://<namespace>.servicebus.windows.net/
```

### 2. Register the transport with a credential mode

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb
        .WithConnectionString("Endpoint=sb://<namespace>.servicebus.windows.net/")
        .UseAadTokenProviderWithManagedIdentity()
        .AddQueueReceiver<PlaceOrder>("orders"));
```

### 3. Grant the identity access

Assign the identity a Service Bus data role on the namespace or entity, such as Azure Service Bus Data Sender and Azure Service Bus Data Receiver.

## Credential modes

Call one mode per `AddAzureServiceBus`. A later call replaces an earlier one, but each call still builds its credential when it runs.

### Choosing a mode

| Mode | Method | Use when |
| --- | --- | --- |
| Managed identity, system-assigned | `UseAadTokenProviderWithManagedIdentity()` | Your application runs on an Azure host with a system-assigned identity. |
| Managed identity, user-assigned | `UseAadTokenProviderWithManagedIdentity(clientId)` | Your application runs on Azure with a user-assigned identity, or on AKS workload identity. |
| Client secret | `UseAadTokenProviderWithSecret(clientId, clientSecret, authority)` | Your application runs outside Azure as an app registration with a secret. |
| Client certificate | `UseAadTokenProviderWithCert(clientId, thumbPrint, authority)` | As for client secret, with a certificate installed in the machine's certificate store. |
| Interactive | `UseAadTokenProviderInteractively(clientId, redirectUri)` | A developer or desktop tool signs in through a browser. |

### Managed identity

```csharp
// system-assigned
asb.UseAadTokenProviderWithManagedIdentity();

// user-assigned: pass the identity's client id
asb.UseAadTokenProviderWithManagedIdentity(clientId: "<managed-identity-client-id>");
```

The credential names the identity itself and never consults the `DefaultAzureCredential` chain. `AZURE_TOKEN_CREDENTIALS` and the `Exclude*` options have no effect on it, and `optBuilder` cannot change which identity is requested.

> **Important:** On a federated-token host such as AKS workload identity, a blank client id falls back to the `AZURE_CLIENT_ID` environment variable, so you authenticate as the workload identity, not the system-assigned one. Pass the client id explicitly to avoid this.

`optBuilder` configures `ManagedIdentityCredentialOptions`, for example the authority host of a sovereign cloud. Name the argument; a bare lambda lands in the `clientId` slot and does not compile.

```csharp
asb.UseAadTokenProviderWithManagedIdentity(
    optBuilder: o => o.AuthorityHost = new Uri("https://login.microsoftonline.us/"));
```

### Client secret

```csharp
asb.UseAadTokenProviderWithSecret(
    clientId: "<app-client-id>",
    clientSecret: builder.Configuration["<secret-key>"],
    authority: "https://login.microsoftonline.com/<tenant-id>/");
```

The tenant id is the first non-empty path segment of `authority`, and deeper segments are ignored, so the `/v2.0` issuer URL from the Azure portal also works. The scheme and host become the credential's `AuthorityHost`.

### Client certificate

```csharp
asb.UseAadTokenProviderWithCert(
    clientId: "<app-client-id>",
    thumbPrint: "<certificate-thumbprint>",
    authority: "https://login.microsoftonline.com/<tenant-id>/",
    validCertsOnly: false); // allow a self-signed certificate
```

The certificate is read from the `My` store, `CurrentUser` then `LocalMachine`, when this line runs at registration. `authority` is parsed as for client secret.

### Interactive

```csharp
asb.UseAadTokenProviderInteractively(
    clientId: "<app-client-id>",
    redirectUri: "http://localhost");
```

This builds an `InteractiveBrowserCredential` from the client id and redirect URI only. It takes no authority, so the sign-in is not scoped to a tenant by this package. The redirect URI must be registered on the app registration.

## Local development

### Default credential fallback

When `clientSecret`, `thumbPrint` or `redirectUri` is null or whitespace, that mode returns a `DefaultAzureCredential` and ignores `clientId` and `authority`. This is the only path that resolves an identity from the host, and it honors `az login`. `optBuilder` configures its `DefaultAzureCredentialOptions`:

```csharp
asb.UseAadTokenProviderInteractively(
    clientId: null,
    redirectUri: null, // falls back to DefaultAzureCredential
    optBuilder: o => o.ExcludeAzureCliCredential = true);
```

> **Warning:** A secret or thumbprint read from configuration that turns out to be missing also takes the fallback. Check the value at startup if a silent switch to an ambient identity is not acceptable.

### Switching by environment

Managed identity fails off Azure, so select the mode by environment and keep one code path:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb =>
    {
        asb.WithConnectionString("Endpoint=sb://<namespace>.servicebus.windows.net/");

        if (builder.Environment.IsDevelopment())
        {
            asb.UseAadTokenProviderInteractively(clientId: null, redirectUri: null); // az login
        }
        else
        {
            asb.UseAadTokenProviderWithManagedIdentity(clientId: "<managed-identity-client-id>");
        }
    });
```

## Configuration

This package binds no configuration section. The endpoint-only connection string can come from the transport's `ConnectionString` key instead of `WithConnectionString`; see [Azure Service Bus configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#configuration). Read credential values from your own configuration keys and pass them to the methods below.

| Method | Description |
| --- | --- |
| `UseAadTokenProviderWithSecret(string clientId, string clientSecret, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null)` | `ClientSecretCredential`; `DefaultAzureCredential` when `clientSecret` is blank. |
| `UseAadTokenProviderWithCert(string clientId, string thumbPrint, string authority, Action<DefaultAzureCredentialOptions> optBuilder = null, bool validCertsOnly = true)` | `ClientCertificateCredential`; `DefaultAzureCredential` when `thumbPrint` is blank. `validCertsOnly: false` accepts self-signed certificates. |
| `UseAadTokenProviderInteractively(string clientId, string redirectUri, Action<DefaultAzureCredentialOptions> optBuilder = null)` | `InteractiveBrowserCredential`; `DefaultAzureCredential` when `redirectUri` is blank. |
| `UseAadTokenProviderWithManagedIdentity(string clientId = null, Action<ManagedIdentityCredentialOptions> optBuilder = null)` | `ManagedIdentityCredential`: user-assigned when `clientId` is given, system-assigned when it is blank. |

For the first three methods, `optBuilder` applies only to the `DefaultAzureCredential` fallback.

## How it works

- Each method calls `ServiceBusOptionsBuilder.AddTokenProvider` with a factory that runs at once, so the credential is built at registration, not at first use.
- `AadTokenProviderFactory` (namespace `Chatter.MessageBrokers.AzureServiceBus.Auth`) builds the credential through `Create(clientId)` and `WithSecret`, `WithCert`, `WithInteractive` or `WithManagedIdentity`. You can call it directly and pass the result to `AddTokenProvider`.
- When the options are built, the credential is applied only if the connection string has no `SharedAccessSignature` and no `SharedAccessKeyName` plus `SharedAccessKey` pair. See [Authentication](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#authentication).
- With a credential applied, the shared `ServiceBusClient` connects to the fully qualified namespace from `Endpoint`.

## Troubleshooting

- **`CredentialUnavailableException` with managed identity**: no managed identity endpoint is reachable, typically on a developer machine. It fails at first token acquisition, possibly after a delay, and never falls through to another credential. Use [Switching by environment](#switching-by-environment).
- **Exception at registration with a certificate**: the thumbprint was not found in either `My` store (`ArgumentException`), or the platform cannot open a store. Check the thumbprint, and pass `validCertsOnly: false` for a self-signed certificate.
- **`ArgumentNullException` at registration with a secret or certificate**: `authority` is blank, not an absolute URL, or has no path segment, so there is no tenant id.
- **Authentication fails with a non-Entra authority**: an authority whose first segment is not the tenant id (Azure AD B2C `/tfp/...`, ADFS, DSTS, or a pasted `/oauth2/v2.0/token` endpoint) yields a wrong tenant id. The credential still builds and the error appears at first token acquisition; what Entra returns then is not guaranteed. Use `https://<authority-host>/<tenant-id>/`.
- **The credential is ignored**: the connection string still carries a shared access key or signature, and SAS takes precedence.
- **Wrong identity on AKS**: a blank managed identity client id picks up `AZURE_CLIENT_ID`; pass the client id.

## Diagnostics

This package emits no telemetry of its own; see [Azure Service Bus diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#diagnostics).

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): In-process Commands, Queries, Events and the Command Pipeline.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): Broker abstractions: receivers, routing, Inbox/Outbox and Recovery.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): The Azure Service Bus transport this package authenticates.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus.Auth/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus.Auth/src/Chatter.MessageBrokers.AzureServiceBus.Auth/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
