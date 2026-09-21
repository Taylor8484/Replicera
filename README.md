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
replicera job add --name development --source development-dataverse --destination development-sql --table account --mode complete
replicera tables add contact --job development
replicera schedule set --job development --interval 00:05:00 --run-on-start
replicera worker --job development
```

The `--secret-env` and `--connection-env` arguments are environment-variable names. A job must already exist in the configuration before using `tables add`.
Use `source add --interactive`, `destination add --interactive`, or `job add --interactive` to prompt for omitted setup values. The prompts request secret-variable names, never secret contents.

`inspect` reads Dataverse and destination metadata but does not enable change tracking or modify destination schema. `sync` performs those configured mutations.
Jobs support three synchronization modes: `complete` (the default) mirrors source rows and columns, including deletions; `noDataLoss` adds and updates while timestamping retained source-deleted rows; and `reload` drops, recreates, and fully loads each table on every run. Newly detected columns are created nullable and trigger a full read so existing rows can be populated. Every table includes `data_load_dte`; `noDataLoss` tables also include `date_source_remove_dte`. Configure the JSON `sync.mode`, or pass `--mode complete|no-data-loss|reload` to `job add`.

Declare known Dataverse column renames under `schema.columnRenames.<table>` to preserve destination values. Destructive drops are dependency-checked and never cascade. A source table is dropped only after a direct Dataverse metadata request confirms that it no longer exists.
Reload mode is intentionally destructive and is intended for development or small tables: a failed load leaves the recreated table available for a rerun but does not restore its previous physical contents.
Use `sync --full --table <name>` to force a full source read. In `complete` mode it performs a controlled replacement; in `noDataLoss` mode it performs a non-destructive full merge. The existing destination data and checkpoint remain recoverable if the transaction fails.
Use `--json` with `inspect`, `sync`, or `status` for machine-readable results. `sync --verbose` writes replication progress to standard error; `sync --log-json` writes the same diagnostics as JSON Lines for central logging.
`status` reports each table's state, last successful sync, latest run type/time, row counts, and sanitized failure details.

For portable automatic synchronization, configure an interval with `schedule set` and leave `replicera worker --job <job>` running. The worker runs one job at a time, waits for the configured interval after each completed attempt, and continues after failed attempts. Press Ctrl+C for a graceful stop. The existing `sync` command remains available for manual runs whether the schedule is enabled or disabled. See the operations guide for the full worker lifecycle and limitations. Multi-job hosting, calendar schedules, containers, and native services are tracked in the [scheduling roadmap](docs/scheduling-roadmap.md).

See `THIRD-PARTY-NOTICES.md` before distributing binaries.
See [`docs/operations.md`](docs/operations.md) for privileges, deployment, recovery, resynchronization, and troubleshooting.
Current platform support and limitations are listed in the [`v0.1 draft release notes`](docs/release-notes/v0.1-draft.md).

## Licensing and Contributions

Replicera Community is open-source software licensed under the [Mozilla Public License 2.0](LICENSE). Commercial use is permitted subject to the MPL 2.0 terms. Replicera may offer separately licensed commercial products or extensions in the future; this does not change the MPL-2.0 licensing of the Community project.

The Community edition is intended to remain a complete, useful replication product. See [Community Principles](COMMUNITY.md) for the open-core boundary and Community feature guarantee. See [Contributing](CONTRIBUTING.md) for contribution standards and the pending contributor agreement model.

## Status

Replicera is under active v0.1 development. Public commands and configuration formats may change until v0.1.
