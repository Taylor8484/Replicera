# ADR 0004: SQL Server staging and checkpoint transactions

- Status: Accepted for initial implementation
- Date: 2026-09-19

## Decision

Use `SqlBulkCopy` into uniquely named staging tables, then explicit set-based `UPDATE`, `INSERT`, and `DELETE` statements. Do not use SQL Server `MERGE`. Acquire a transaction-owned `sp_getapplock` keyed by job and table. Store managed-object metadata, sync history, schema history, and checkpoints in the `replicera` schema.

Data mutations and checkpoint advancement occur in one SQL transaction. Initial loads delete the managed target and apply all staged pages inside that uncommitted transaction; readers never observe a committed partial replacement. The uniquely named staging table is created, reused, and dropped in the same transaction.

## Consequences

Retry after rollback replays the same source page safely. SQL Server transaction rollback removes an uncommitted staging table after failure or process termination. Identifier quoting and staging cleanup are provider responsibilities. Integration tests must cover deadlocks, command failure, cancellation, lock exclusion, and a process ending between data application and commit.
