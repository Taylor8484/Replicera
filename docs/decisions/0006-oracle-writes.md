# ADR 0006: Oracle staging and checkpoint transactions

## Status

Accepted

## Decision

Use ODP.NET array binding to load each source page into a uniquely named global temporary table created with `ON COMMIT DELETE ROWS`. Apply upserts with a set-based Oracle `MERGE`, apply physical deletes separately, and clear staging rows with transactional `DELETE` between pages.

Acquire a `FOR UPDATE NOWAIT` lock on the managed-table metadata row. Keep target mutations, run metrics, and the opaque checkpoint in the same Oracle transaction. Create and drop the temporary-table definition outside that transaction because Oracle DDL commits implicitly; a process failure can therefore leave only an empty, uniquely named staging definition, never uncommitted rows or an advanced checkpoint. Later runs drop such definitions once they are an hour old; Oracle refuses to drop a temporary table another session is using, so active runs keep theirs. Dropped tables bypass the recycle bin, and a lock release that fails discards the pooled session so its session lock cannot outlive the run.

## Consequences

The command-line runner holds a session-scoped `DBMS_LOCK` lock per job and table from before schema inspection until the checkpoint commits, and confirms immediately before commit that the session still holds it; a lost lock rolls back the transaction without advancing the checkpoint. Because `CREATE TABLE` commits implicitly, the managed-table metadata row is committed before the table is created, so a failure after the DDL leaves a table Replicera still recognizes as its own on the next run. Metadata creation and upgrades are serialized with a `DBMS_LOCK` lock and skipped when the metadata is already current.

Do not use `OracleBulkCopy`: its work is independent of application transactions and cannot preserve Replicera's atomic checkpoint boundary. Array binding keeps page writes set-based while participating in the active transaction. Integration tests must cover rollback, lock exclusion, supported value families, schema safety, and checkpoint/metric commits against Oracle AI Database Free 26ai.
