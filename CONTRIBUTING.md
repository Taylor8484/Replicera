# Contributing to Replicera Community

Thank you for contributing. Read the README, operations guide, Community principles, and relevant architecture decisions before changing code. Replicera Community is licensed under MPL-2.0, and contributions must be compatible with that license.

## Contributor Rights and Provenance

Contributors retain copyright ownership of their contributions. You must have the legal right to submit your work and must not submit code copied from incompatible, unauthorized, or unknown sources. Identify adapted code and its license in the pull request. New third-party dependencies require a documented license-compatibility review and an update to `THIRD-PARTY-NOTICES.md` when applicable.

Accepted Community contributions remain available in Replicera Community under MPL-2.0. Under the project's Community contribution guarantee, using a contribution in a commercial Replicera product will not cause that contribution to be removed from Community solely to make it commercially exclusive. This is a project governance commitment; it does not modify or replace MPL 2.0.

## Contributor License Agreement Status

The project intends to use a Contributor License Agreement rather than copyright assignment. Contributors will continue to own their contributions. The intended CLA will grant the Replicera project broad, perpetual rights to use, modify, distribute, sublicense, and incorporate contributions into Replicera products, including potentially separately licensed commercial editions.

No approved CLA or signing process exists yet. This document is not a CLA, and contributors have not agreed to additional terms merely by reading it or opening a pull request. The eventual legal text remains pending legal review and should be based on a recognized framework such as the Harmony Agreements or another lawyer-reviewed agreement. The project will publish the approved text and acceptance process before representing any contributor as bound by it.

## Engineering Standards

- Keep `Replicera.Core` provider-neutral and free of Dataverse SDK types, destination-specific SQL, and Windows-only assumptions.
- Put database-specific SQL, type mappings, locking, and schema behavior in the corresponding provider.
- Preserve cross-platform behavior. Linux is a first-class development and build environment, and changes must not break Windows support.
- Add appropriate tests for functional changes and keep the existing suite passing.
- Add correctness and failure tests for changes to checkpointing, synchronization, retries, deletes, transaction boundaries, or recovery.
- Preserve safeguards around schema ownership and destructive operations. Changes that weaken them require explicit design justification and additional review.
- Document public APIs, configuration changes, operational requirements, and significant behavior.
- Do not introduce telemetry or external data collection without explicit project approval and public documentation.
- Treat authentication, authorization, secret handling, SQL construction, dependency loading, and data exposure as security-sensitive areas requiring additional review.
- Never commit credentials, secrets, tokens, certificates, connection strings containing secrets, or production data. Do not include them in fixtures, logs, issues, or pull requests.

## Pull Requests

Create focused changes and use short, imperative commit subjects. Pull requests should explain the resulting behavior, identify relevant issues or design decisions, describe failure and recovery behavior where applicable, disclose new dependencies and licenses, and list validation performed.

Run these checks from the repository root:

```sh
dotnet restore Replicera.sln --locked-mode
dotnet build Replicera.sln --no-restore
dotnet test Replicera.sln --no-build --filter "Category!=Credentialed&Category!=SqlServerIntegration&Category!=PostgreSqlIntegration&Category!=OracleIntegration&Category!=DataverseSqlEndToEnd&Category!=DataverseOracleEndToEnd&Category!=Scale"
dotnet format Replicera.sln --no-restore --verify-no-changes
```

This command runs the credential-free suite. Run the relevant scripts under `scripts/` for database, Dataverse, or scale integration changes. Mark and document tests requiring external services so the normal suite remains deterministic.
