# Replicera

Replicera is an early-stage, self-hosted .NET CLI for maintaining relational mirrors of selected Microsoft Dataverse tables. SQL Server, PostgreSQL, and Oracle are supported destination providers.

The repository contains the CLI, Dataverse adapter, destination providers, automated tests, operations documentation, and architecture decisions.

## Prerequisites

- .NET SDK specified by `global.json`
- Git
- A container runtime or remote SQL Server, PostgreSQL, or Oracle instance for provider integration tests
- A non-production Dataverse environment for credentialed tests

The Dataverse pump should use a dedicated application user. Give it System Customizer for metadata operations and organization-level Read on the tables being replicated; it does not require source-row write privileges or System Administrator. See the operations guide for the full role model.

For initial role provisioning, temporarily grant System Administrator and run `replicera source bootstrap-permissions --name <source>`. The command assigns System Customizer and the generated reader role. Remove System Administrator manually after it succeeds and verify access with `inspect` or `sync`.

## Build

```sh
dotnet restore Replicera.sln
dotnet build Replicera.sln --no-restore
dotnet test Replicera.sln --no-build
dotnet run --project src/Replicera.Cli -- --help
```

Published builds use the executable name `replicera`. Run `replicera --version` to report the version embedded by the release build.

## Release Packages

Pushing a `v*` tag builds a self-contained Linux `.tar.gz` and Windows `.zip`, publishes matching `.sha256` files, and creates a GitHub release. Each archive contains the executable, README, operations guide, license, and third-party notices. Packages can also be built locally:

```sh
python3 scripts/package_release.py --version 0.1.0 --runtime linux-x64
python3 scripts/package_release.py --version 0.1.0 --runtime win-x64
```

Verify the adjacent `.sha256` file, extract the archive into a versioned directory, and invoke `replicera` (`replicera.exe` on Windows) from that directory. Upgrade by extracting the new version beside the old one, reusing the external configuration and environment variables, and switching the scheduled command after `replicera --version` succeeds.

Run the database integration suites with Docker:

```sh
scripts/run-sqlserver-integration-tests.sh
scripts/run-postgresql-integration-tests.sh
scripts/run-oracle-integration-tests.sh
scripts/run-oracle-end-to-end-tests.sh
scripts/run-scale-test.sh
scripts/run-dataverse-integration-tests.sh
scripts/run-end-to-end-tests.sh
```

The database scripts start and remove temporary SQL Server 2022, PostgreSQL 17, and Oracle AI Database Free 26ai containers. To use an existing non-production server, set the corresponding `REPLICERA_SQL_TEST_CONNECTION_STRING`, `REPLICERA_POSTGRES_TEST_CONNECTION_STRING`, or `REPLICERA_ORACLE_TEST_CONNECTION_STRING`. The scale script streams a logical 512 MiB dataset with a 256 MiB managed-heap limit and enforces a 5,000-record/s baseline. The Dataverse and end-to-end scripts load `.env.local` by default, or the file named by `REPLICERA_ENV_FILE`. The SQL Server and Oracle end-to-end scripts create and clean up a Dataverse account while verifying initial, update, and delete replication. Never use a production Dataverse environment.

Configuration files contain environment-variable names rather than secret values. Client-secret authentication reads the secret directly from the configured variable. Certificate authentication reads a base64-encoded PKCS#12 document and, when needed, its password from a second environment variable.

Copy the matching file from `examples/` to `replicera.local.json`, replace the non-secret identifiers and URL, and set the named environment variables. Inspect before synchronizing:

```sh
dotnet run --project src/Replicera.Cli -- inspect account --config replicera.local.json
dotnet run --project src/Replicera.Cli -- sync --job development --config replicera.local.json
dotnet run --project src/Replicera.Cli -- status --job development --config replicera.local.json
```

Configuration commands can also add sources, destinations, and selected tables without placing secret values in the file:

```sh
replicera source add --name development-dataverse --url https://example.crm.dynamics.com \
  --tenant-id <guid> --client-id <guid> --secret-env REPLICERA_DATAVERSE_CLIENT_SECRET
replicera destination add --name development-sql --connection-env REPLICERA_SQL_CONNECTION_STRING
replicera destination add --name development-postgres --provider postgresql \
  --connection-env REPLICERA_POSTGRES_CONNECTION_STRING
replicera destination add --name development-oracle --provider oracle \
  --connection-env REPLICERA_ORACLE_CONNECTION_STRING
replicera job add --name development --source development-dataverse --destination development-sql --table account
replicera tables add contact --job development
```

The `--secret-env` and `--connection-env` arguments are environment-variable names. A job must already exist in the configuration before using `tables add`.
Use `source add --interactive`, `destination add --interactive`, or `job add --interactive` to prompt for omitted setup values. The prompts request secret-variable names, never secret contents.

`inspect` reads Dataverse and destination metadata but does not enable change tracking or modify destination schema. `sync` performs those configured mutations.
Use `sync --full --table <name>` for a controlled rebuild. The existing destination data and checkpoint remain recoverable if the rebuild transaction fails.
Use `--json` with `inspect`, `sync`, or `status` for machine-readable results. `sync --verbose` writes replication progress to standard error; `sync --log-json` writes the same diagnostics as JSON Lines for central logging.
`status` reports each table's state, last successful sync, latest run type/time, row counts, and sanitized failure details.

See `THIRD-PARTY-NOTICES.md` before distributing binaries.
See [`docs/operations.md`](docs/operations.md) for privileges, deployment, recovery, resynchronization, and troubleshooting.
Current platform support and limitations are listed in the [`v0.1 draft release notes`](docs/release-notes/v0.1-draft.md).

## Status

Replicera is under active v0.1 development. Public commands and configuration formats may change until v0.1.
