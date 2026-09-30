# Operations Guide

## Configuration

Start with `replicera init --config replicera.local.json` or copy the matching SQL Server, PostgreSQL, or Oracle file from `examples/`. Configuration stores environment-variable names, never secret values. Keep the local file outside source control; `replicera.local.json` and `.env*` are ignored.

Client-secret authentication expects the secret in `authentication.secretEnvironmentVariable`. Certificate authentication expects a base64-encoded PKCS#12 document in that variable and an optional password in `certificatePasswordEnvironmentVariable`. Destinations read their connection string from the variable named by `connectionStringEnvironmentVariable`; select one with `provider: "sqlserver"`, `"postgresql"`, or `"oracle"`.

Run `replicera inspect <table> --job <job>` before the first sync. Inspection reads metadata and reports proposed changes without enabling change tracking or changing the destination.

Set `sync.mode` on each job:

- `complete` is the default mirror mode. It applies inserts, updates, row deletions, column additions, and column removals.
- `noDataLoss` applies inserts, updates, and column additions while retaining source-removed rows and columns.
- `reload` drops and recreates each managed destination table from current metadata, then loads it from a full Dataverse read on every run. This is intended for development and smaller tables.

`reload` is intentionally destructive: the drop/recreate schema step commits before the replacement data load. If that load fails, rerun the job to populate the newly created table; the previous physical table is not restored. Views, foreign keys, grants, or other objects that depend on the managed table can also prevent the drop and must be managed explicitly.

Every managed table contains `data_load_dte`, which is refreshed whenever a row is inserted or updated. Tables using `noDataLoss` also contain `date_source_remove_dte`; a Dataverse delete stamps that column instead of deleting the row, and a later upsert clears it. Before a no-data-loss full scan, Replicera provisionally stamps existing rows and then clears the stamp as each current source row is upserted. This also identifies destination-only rows when incremental deletion history is unavailable, while preserving an earlier removal date. Columns discovered after a table already contains data are always added as nullable destination columns. Replicera performs a full source read after adding one, allowing historical rows to receive values while retaining `NULL` where the source has no value. In `noDataLoss` mode that read merges into the destination rather than deleting destination-only rows. Every destination column other than the primary key is nullable, regardless of the Dataverse required level. Required levels are business rules enforced by Dataverse, and records created through the API, imports, or before a requirement was introduced can still contain empty values; keeping destination columns nullable means a required-level change in Dataverse never breaks replication. When an existing managed table contains a non-key `NOT NULL` column, schema reconciliation relaxes it automatically and records the change in schema history. SQL Server does not allow changing the nullability of most indexed columns, so if a user-created index covers such a column, schema reconciliation fails until the index is dropped or the column is relaxed manually. Increasing the decimal places of a Dataverse decimal or currency column widens the destination column; Dataverse limits these values to 12 and 15 digits before the decimal point, so existing values always fit. Oracle cannot alter a populated `NUMBER` column this way, so Replicera copies the values into a replacement column, as it does when a text column grows beyond 2,000 characters, and then performs a full read. Currency columns whose precision comes from the organization pricing precision or the transaction currency, rather than the column's own setting, are created with four decimal places so that no value is rounded.

The last successful mode is stored with each managed table. Changing from `noDataLoss` to `complete` automatically forces a full replacement so deletes previously retained by `noDataLoss` cannot survive unnoticed. If a configured Dataverse table is absent from the published table inventory during a scheduled sync, Replicera automatically issues a direct metadata request for that logical name. Only a recognized Dataverse table-not-found response confirms removal: `complete` and `reload` then drop the managed destination table, while `noDataLoss` retains it and records the condition. A successful direct lookup continues synchronization; authorization, authentication, throttling, connectivity, and ambiguous metadata errors stop safely without dropping the destination.

The synchronization mode is authoritative for row, column, and table deletion. A column that still exists in Dataverse but can no longer be replicated, for example because it is no longer valid for read operations, is never dropped: its destination column and existing values are retained, it stops receiving updates, and `inspect` reports it. Dataverse cannot change a column's type in place, so a destination column whose type no longer matches its source column is treated as replaced, as is a date/time column whose behavior now needs a different column type (for example a change to date only, or on PostgreSQL and Oracle a change between user local and time-zone independent). The mode decides what happens to the old column: `complete` drops it, and `noDataLoss` renames it to `<column>_replaced_<yyyymmdd>` so rows retained there keep their values. The new column is then added and a full read fills it. If the preserved name is already taken, or the primary key type changed, the table is blocked (exit code 6) until the conflict is resolved manually. A source column that becomes narrower, such as a shorter maximum length or fewer decimal places, keeps its wider destination column, and `inspect` reports the difference.

The older `schema.dropColumns`, `schema.dropTables`, and `schema.incompatibleChanges` fields remain readable for configuration compatibility but do not override the selected mode. The other schema settings still control table creation, column addition, and compatible widening.

Column renames must be declared explicitly because Dataverse metadata cannot reliably distinguish a rename from an unrelated drop and addition. Mappings are scoped by table logical name:

```json
"schema": {
  "columnRenames": {
    "account": {
      "old_logical_name": "new_logical_name"
    }
  }
}
```

`complete` and `noDataLoss` rename the existing destination column and preserve its values. `reload` recreates the table directly from current source metadata, so rename mappings require no DDL action in that mode. Keep an applied mapping in configuration; mappings are idempotent and protect subsequent deployments from interpreting stale layouts as an unrelated drop/add.

Replicera holds one provider-level lock from before schema inspection until the row transaction and checkpoint commit. Destructive schema changes run a dependency preflight. External foreign keys, views, triggers, custom indexes, or grants block a table recreation; column-specific constraints, indexes, foreign keys, and dependent objects block a column drop. Replicera reports the dependencies and never uses a cascading drop. Remove and recreate those objects explicitly if the destructive operation is intended.

Currently unsupported Dataverse attribute categories are `CalendarRules`, `EntityName`, `ManagedProperty`, `PartyList`, and `Virtual`. File and image attributes are represented through unsupported specialized or virtual metadata and are therefore skipped. Derived child attributes such as formatted-name companions and attributes marked not valid for read are also skipped regardless of their underlying type.

## Required Privileges

Run Replicera as a dedicated Dataverse application user. The recommended pump identity has the System Customizer role for table-metadata operations plus a custom reader role with organization-level Read on every replicated table and any referenced tables needed for lookups. `RetrieveEntityChanges` rejects User, Business Unit, and Parent: Child Business Unit read depths; Read must be set to Organization for each replicated table. This deliberately allows Replicera to enable change tracking while keeping source rows read-only: do not grant Create, Write, Delete, Assign, or Share unless a separately configured feature requires them. System Administrator is not required.

`inspect` only reports whether change tracking must be enabled. `sync` performs the metadata update when the job's change-tracking policy allows it. If metadata mutation is managed outside Replicera, omit System Customizer, enable tracking administratively, and configure the job to require it already enabled. Use a dedicated non-production identity for credentialed validation.

To bootstrap the reader role, temporarily assign System Administrator to the application user and choose the tables the role should cover. The recommended form lists the replicated tables, and any lookup target tables you also want readable, in a JSON array of logical names (see `examples/bootstrap-tables.example.json`):

```sh
replicera source bootstrap-permissions --name <source> --tables-file bootstrap-tables.json
```

To grant Read on every table in the environment instead, pass `--all-tables`. This gives the pump identity organization-wide read access to all Dataverse data, including tables that are never replicated, so a leaked credential exposes the whole environment; use it only where that exposure is acceptable. One of `--tables-file` or `--all-tables` is required.

The command creates or updates `Replicera Pump Reader`, grants Organization-level Read on the selected tables, and assigns the role to the calling application user. With `--tables-file`, every listed table must exist in published metadata and support Organization-level Read, otherwise the command fails without changing the role. It also assigns the built-in System Customizer role. It preserves privileges already present in a same-named role, including Read privileges granted by an earlier run, and is safe to rerun; to narrow an existing role, remove its extra privileges in Dataverse or bootstrap a new role with `--role-name`. New tables are not covered automatically; add them to the tables file and rerun bootstrap. Remove System Administrator manually, leave System Customizer and Replicera Pump Reader assigned, and run `inspect` or `sync` to verify the steady-state permissions. Replicera never removes its own administrator role.

For release validation, `scripts/verify-expired-dataverse-checkpoint.sh` consumes the ignored retained-checkpoint state. Run it only after the `verifyAfterUtc` recorded in that file; it verifies that Dataverse reports the natural expiry as a full-resynchronization condition without printing the token.

The SQL principal needs database connectivity plus permission to create the `replicera` schema and its metadata tables on first use. It also needs `CREATE TABLE`, schema `ALTER`, and `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on Replicera-managed destination objects. The principal must be allowed to call `sys.sp_getapplock`. Scope the account to one destination database and do not grant server administration roles.

The PostgreSQL role needs `CONNECT` and `CREATE` on the destination database, `USAGE` and `CREATE` on the `public` schema, and ownership or DDL/DML privileges for Replicera-managed objects. Database-level `CREATE` allows the first run to create the `replicera` schema. PostgreSQL advisory locks require no special role. Scope the role to one destination database; permission to create databases is needed only by the integration-test harness.

The Oracle user owns its metadata and destination tables. Grant `CREATE SESSION`, `CREATE TABLE`, `EXECUTE ON SYS.DBMS_LOCK`, and sufficient tablespace quota; for a dedicated Replicera schema, `UNLIMITED TABLESPACE` is the simplest development grant. No DBA role or cross-schema privilege is required. Use service `FREEPDB1` for the Oracle Free container, for example `User Id=REPLICERA;Password=...;Data Source=localhost:1521/FREEPDB1`. Replicera uses session-specific global temporary staging tables and a session-scoped `DBMS_LOCK` lock per job and table.

## Deployment

CI produces framework-dependent and self-contained `linux-x64` and `win-x64` artifacts. Tagged releases provide versioned self-contained archives and adjacent SHA-256 files. Verify the checksum, unpack the archive into a versioned application directory, and run `replicera --version` before switching the scheduled command. Keep configuration and credentials outside that directory so upgrades can reuse them. Framework-dependent builds require the .NET 10 runtime; self-contained builds include it.

Run one process per scheduled invocation. Replicera uses a database application lock to reject overlapping runs for the same job and table before schema inspection begins. Capture standard output and error, preserve the numeric exit code, and use `--json` for scheduler results. Add `--log-json` to write structured JSON Lines diagnostics to standard error; diagnostic events omit row values, connection strings, and change tokens.

Each scheduled invocation retrieves published Dataverse metadata, then processes configured tables in order. For each table, Replicera acquires its lock, reconciles schema, performs the required incremental or full read, commits destination rows and the checkpoint, releases the lock, and moves to the next table. Tables committed before a later failure remain committed; the failing table retains its last safe checkpoint or is marked for full resynchronization, and subsequent tables wait for the next invocation. Configure the scheduler not to start a new instance while the previous invocation is still running. The per-table lock preserves safety if overlap occurs, but it does not serialize an entire multi-table job as one unit.

### Portable foreground worker

Replicera can own the interval schedule while remaining a portable foreground application. Configure a job and start its worker:

```sh
replicera schedule set --job production --interval 00:05:00 --run-on-start
replicera schedule show --job production
replicera worker --job production --log-json
```

Use `--wait-first` instead of `--run-on-start` when the worker should wait one interval before its first synchronization. The interval must be between one second and seven days. It begins after each synchronization attempt completes, so a long run never overlaps itself and missed intervals do not accumulate. A failed attempt is reported and the worker tries again after the next interval. The worker reloads the configuration before every cycle, so changes to the job, its tables, its mode, and its schedule take effect on the next cycle without a restart. If the edited configuration fails validation, the worker reports a `configurationInvalid` event and continues with its last valid schedule. If the job is removed from the configuration, the worker stops with exit code `2`.

The first worker version runs one job per process and must remain open. Press Ctrl+C to cancel an active synchronization through the normal checkpoint-safe cancellation path and stop the worker. Manual `replicera sync` remains available while a worker exists. Destination table locks prevent the two processes from mutating the same job/table simultaneously; a collision is reported as a synchronization failure and the worker tries again on its next interval.

Pause scheduled synchronization without affecting manual synchronization, and resume it later:

```sh
replicera schedule disable --job production
replicera schedule enable --job production
```

A running worker notices a disabled schedule on its next cycle, reports `scheduleDisabled`, and stays idle, checking again every minute or every interval if that is shorter, until the schedule is enabled. A worker started while its schedule is disabled starts idle, so a service manager that restarts the process does not enter a restart loop. `schedule enable` keeps the existing interval and start behavior.

The worker never initiates `--full` automatically. If status reports `ResyncRequired`, stop or leave the worker running, perform the controlled manual recovery described below, and then allow the next scheduled incremental run to proceed.

## Recovery and Resynchronization

Destination changes, run metrics, and the new checkpoint commit in one database transaction. Cancellation or failure rolls back row changes and retains the last successful checkpoint. Correct the reported cause and rerun the same command; incremental replay is idempotent.

Exit code `8` or table state `ResyncRequired` means Dataverse rejected the stored checkpoint. Run a controlled replacement:

```sh
replicera sync --job <job> --table <table> --full --verbose
```

In `complete` mode the replacement deletes and reloads only the managed table inside a transaction. In `noDataLoss` mode it performs a full merge and retains destination-only rows. Failure restores the previous committed rows and checkpoint. Do not edit checkpoints or Replicera metadata manually.

## Troubleshooting

Dataverse requests are retried up to three times when Dataverse reports service-protection throttling, a request timeout, or a server error, or when the connection fails. Throttled requests wait for the `Retry-After` interval Dataverse returns, up to five minutes; other retries back off exponentially from two seconds with jitter, up to one minute. Requests that create or associate records are repeated only after throttling, because a request that failed after reaching Dataverse may already have taken effect. Other rejections, including missing privileges, are reported immediately without retrying.

- **Exit 2:** validate JSON, environment-variable names, job references, and batch size.
- **Exit 3 or 4:** verify the Dataverse URL, application user, credentials, organization-level table privileges, and change-tracking setting.
- **Exit 5:** verify SQL connectivity, database selection, TLS settings, and database permissions.
- **Exit 6:** run `inspect`; resolve unmanaged-table collisions, rename-mapping conflicts, primary key type changes, or a taken `<column>_replaced_<yyyymmdd>` name explicitly.
- **Exit 7:** check the failed run and table state, correct the transient or destination error, then rerun.
- **Exit 8:** perform the controlled full resynchronization above.

Use `replicera status --job <job> --json` to inspect table state and last success. Error output intentionally omits raw SDK and SQL messages because they may contain credentials, connection details, row values, or change tokens.
