# Repository Guidelines

## Project Structure & Module Organization

Replicera is a cross-platform .NET CLI that mirrors selected Microsoft Dataverse tables into relational databases. `SPEC.md` defines the draft v0.1 requirements.

Use the existing project layout:

- `src/Replicera.Core/`: neutral models, interfaces, replication, and schema logic.
- `src/Replicera.Dataverse/`: authentication, metadata discovery, and change tracking.
- `src/Replicera.Cli/`: commands and configuration.
- `src/Providers/`: SQL Server, PostgreSQL, and Oracle implementations.
- `tests/`: unit, provider, Dataverse, and integration test projects.

Keep destination-specific SQL and type names out of the core engine.

## Build, Test, and Development Commands

Use the SDK pinned in `global.json`. From the repository root, run:

```sh
dotnet restore Replicera.sln
dotnet build Replicera.sln --no-restore
dotnet test Replicera.sln --no-build
dotnet run --project src/Replicera.Cli -- --help
```

Database and Dataverse integration-test scripts are under `scripts/`; run them only against disposable containers or non-production environments. Development must work on Linux without Windows-only tools.

## Coding Style & Naming Conventions

Follow `.editorconfig`: use four-space indentation, PascalCase for types and public members, camelCase for parameters and locals, and `I`-prefixed interfaces such as `IDatabaseProvider`. Match namespaces to project boundaries. Use portable path APIs and keep platform-specific integrations behind abstractions. Builds treat warnings as errors.

## Testing Guidelines

Tests use xUnit. Name test projects `Replicera.*.Tests` and use descriptive behavior-based test names. Cover the acceptance scenarios in `SPEC.md` section 39, especially checkpoint advancement after commit, interrupted-run recovery, token expiry, schema safety, and concurrent execution. Use non-production Dataverse environments and disposable or explicitly designated test databases for integration tests.

## Commit & Pull Request Guidelines

Use short, imperative commit subjects. PRs should describe the change, reference relevant specification sections and issues, report validation performed, and explain configuration or behavior changes.

## Security & Configuration

Never commit credentials, connection strings, private keys, access tokens, or Dataverse change tokens. Keep local values in ignored files such as `.env.local` and `replicera.local.json`; configuration examples must contain placeholders or environment-variable names only. Do not log secrets. Preserve non-destructive schema defaults and advance checkpoints only after destination changes commit successfully.
