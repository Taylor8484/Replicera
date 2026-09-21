# Operations Guide

## Configuration

Start with `replicera init --config replicera.local.json` or copy the matching SQL Server, PostgreSQL, or Oracle file from `examples/`. Configuration stores environment-variable names, never secret values. Keep the local file outside source control; `replicera.local.json` and `.env*` are ignored.

Client-secret authentication expects the secret in `authentication.secretEnvironmentVariable`. Certificate authentication expects a base64-encoded PKCS#12 document in that variable and an optional password in `certificatePasswordEnvironmentVariable`. Destinations read their connection string from the variable named by `connectionStringEnvironmentVariable`; select one with `provider: "sqlserver"`, `"postgresql"`, or `"oracle"`.

Run `replicera inspect <table> --job <job>` before the first sync. Inspection reads metadata and reports proposed changes without enabling change tracking or changing the destination.

## Required Privileges

Run Replicera as a dedicated Dataverse application user. The recommended pump identity has the System Customizer role for table-metadata operations plus a custom reader role with organization-level Read on every replicated table and any referenced tables needed for lookups. `RetrieveEntityChanges` rejects User, Business Unit, and Parent: Child Business Unit read depths; Read must be set to Organization for each replicated table. This deliberately allows Replicera to enable change tracking while keeping source rows read-only: do not grant Create, Write, Delete, Assign, or Share unless a separately configured feature requires them. System Administrator is not required.

`inspect` only reports whether change tracking must be enabled. `sync` performs the metadata update when the job's change-tracking policy allows it. If metadata mutation is managed outside Replicera, omit System Customizer, enable tracking administratively, and configure the job to require it already enabled. Use a dedicated non-production identity for credentialed validation.

To bootstrap the reader role, temporarily assign System Administrator to the application user and run:

```sh
replicera source bootstrap-permissions --name <source>
```

The command creates or updates `Replicera Pump Reader`, grants Organization-level Read for every current table whose Read privilege supports that depth, and assigns the role to the calling application user. It also assigns the built-in System Customizer role. It preserves unrelated privileges already present in a same-named role and is safe to rerun. New tables are not covered automatically; rerun bootstrap after adding tables. Remove System Administrator manually, leave System Customizer and Replicera Pump Reader assigned, and run `inspect` or `sync` to verify the steady-state permissions. Replicera never removes its own administrator role.

For release validation, `scripts/verify-expired-dataverse-checkpoint.sh` consumes the ignored retained-checkpoint state. Run it only after the `verifyAfterUtc` recorded in that file; it verifies that Dataverse reports the natural expiry as a full-resynchronization condition without printing the token.

The SQL principal needs database connectivity plus permission to create the `replicera` schema and its metadata tables on first use. It also needs `CREATE TABLE`, schema `ALTER`, and `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on Replicera-managed destination objects. The principal must be allowed to call `sys.sp_getapplock`. Scope the account to one destination database and do not grant server administration roles.

The PostgreSQL role needs `CONNECT` and `CREATE` on the destination database, `USAGE` and `CREATE` on the `public` schema, and ownership or DDL/DML privileges for Replicera-managed objects. Database-level `CREATE` allows the first run to create the `replicera` schema. PostgreSQL advisory locks require no special role. Scope the role to one destination database; permission to create databases is needed only by the integration-test harness.

The Oracle user owns its metadata and destination tables. Grant `CREATE SESSION`, `CREATE TABLE`, and sufficient tablespace quota; for a dedicated Replicera schema, `UNLIMITED TABLESPACE` is the simplest development grant. No DBA role or cross-schema privilege is required. Use service `FREEPDB1` for the Oracle Free container, for example `User Id=REPLICERA;Password=...;Data Source=localhost:1521/FREEPDB1`. Replicera uses session-specific global temporary staging tables and row locks on its own metadata.

## Deployment

CI produces framework-dependent and self-contained `linux-x64` and `win-x64` artifacts. Tagged releases provide versioned self-contained archives and adjacent SHA-256 files. Verify the checksum, unpack the archive into a versioned application directory, and run `replicera --version` before switching the scheduled command. Keep configuration and credentials outside that directory so upgrades can reuse them. Framework-dependent builds require the .NET 10 runtime; self-contained builds include it.

Run one process per scheduled invocation. Replicera uses a database application lock to reject overlapping runs for the same job and table. Capture standard output and error, preserve the numeric exit code, and use `--json` for scheduler results. Add `--log-json` to write structured JSON Lines diagnostics to standard error; diagnostic events omit row values, connection strings, and change tokens.

## Recovery and Resynchronization

Destination changes, run metrics, and the new checkpoint commit in one database transaction. Cancellation or failure rolls back row changes and retains the last successful checkpoint. Correct the reported cause and rerun the same command; incremental replay is idempotent.

Exit code `8` or table state `ResyncRequired` means Dataverse rejected the stored checkpoint. Run a controlled replacement:

```sh
replicera sync --job <job> --table <table> --full --verbose
```

The replacement deletes and reloads only the managed table inside a transaction. Failure restores the previous committed rows and checkpoint. Do not edit checkpoints or Replicera metadata manually.

## Troubleshooting

- **Exit 2:** validate JSON, environment-variable names, job references, and batch size.
- **Exit 3 or 4:** verify the Dataverse URL, application user, credentials, organization-level table privileges, and change-tracking setting.
- **Exit 5:** verify SQL connectivity, database selection, TLS settings, and database permissions.
- **Exit 6:** run `inspect`; resolve unmanaged-table collisions or incompatible schema changes explicitly.
- **Exit 7:** check the failed run and table state, correct the transient or destination error, then rerun.
- **Exit 8:** perform the controlled full resynchronization above.

Use `replicera status --job <job> --json` to inspect table state and last success. Error output intentionally omits raw SDK and SQL messages because they may contain credentials, connection details, row values, or change tokens.
