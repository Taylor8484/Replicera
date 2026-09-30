# ADR 0004: SQL Server staging and checkpoint transactions

- Status: Accepted for initial implementation
- Date: 2026-09-19

## Decision

Use `SqlBulkCopy` into uniquely named staging tables, then explicit set-based `UPDATE`, `INSERT`, and `DELETE` statements. Do not use SQL Server `MERGE`. Acquire a transaction-owned `sp_getapplock` keyed by job and table. Store managed-object metadata, sync history, schema history, and checkpoints in the `replicera` schema.

Data mutations and checkpoint advancement occur in one SQL transaction. Initial loads delete the managed target and apply all staged pages inside that uncommitted transaction; readers never observe a committed partial replacement. The uniquely named staging table is created, reused, and dropped in the same transaction.

## Consequences

The command-line runner also holds a session-owned `sp_getapplock` for the same job and table from before schema inspection until the checkpoint commits. Immediately before committing, Replicera confirms with `APPLOCK_MODE` that the session still holds it. If the lock was lost, for example because the session was terminated or failed over, the transaction rolls back, the checkpoint is not advanced, and the run fails with exit code `7`. Lock resource names are `replicera:<job>:<table>`; names longer than the 255-character `sp_getapplock` limit use a SHA-256 digest of the job and table instead of truncation, so distinct tables never share a lock. Creation and upgrade of the `replicera` metadata schema are serialized with a transaction-owned `replicera:metadata` application lock so concurrent first runs cannot race.

Retry after rollback replays the same source page safely. SQL Server transaction rollback removes an uncommitted staging table after failure or process termination. Identifier quoting and staging cleanup are provider responsibilities. Integration tests must cover deadlocks, command failure, cancellation, lock exclusion, and a process ending between data application and commit.
