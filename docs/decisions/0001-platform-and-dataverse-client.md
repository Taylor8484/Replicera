# ADR 0001: .NET 10 and Dataverse ServiceClient

- Status: Accepted
- Date: 2026-09-19

## Decision

Target .NET 10, the current LTS release, and pin SDK `10.0.401` with feature-band roll-forward. Use `Microsoft.PowerPlatform.Dataverse.Client.ServiceClient` and the SDK organization service messages for v0.1. Use `RetrieveEntityChangesRequest` for initial and incremental change feeds and treat its `DataToken` as opaque.

## Rationale

.NET 10 is supported through November 2028. Microsoft's current Dataverse guidance recommends `ServiceClient` for new cross-platform .NET clients; it uses MSAL and exposes metadata and change-tracking messages required by Replicera. Keeping SDK objects inside `Replicera.Dataverse` prevents them from leaking into the core.

## Consequences

The CLI requires a .NET 10 runtime unless published self-contained. Credentialed tests must validate SDK behavior on Linux. A later Web API adapter remains possible behind the same source contracts.

## Validation

On 2026-09-20, credentialed Linux tests authenticated with a client secret, retrieved `account` metadata, completed a tokenless change feed, and reused its opaque terminal token for an incremental request. An invalid checkpoint returned Dataverse `InvalidArgument` (`0x80040203`); Replicera classifies that code as an expired checkpoint only when a checkpoint-bearing change request fails.
