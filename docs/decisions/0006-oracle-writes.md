# ADR 0006: Oracle staging and checkpoint transactions

## Status

Accepted

## Decision

Use ODP.NET array binding to load each source page into a uniquely named global temporary table created with `ON COMMIT DELETE ROWS`. Apply upserts with a set-based Oracle `MERGE`, apply physical deletes separately, and clear staging rows with transactional `DELETE` between pages.

Acquire a `FOR UPDATE NOWAIT` lock on the managed-table metadata row. Keep target mutations, run metrics, and the opaque checkpoint in the same Oracle transaction. Create and drop the temporary-table definition outside that transaction because Oracle DDL commits implicitly; a process failure can therefore leave only an empty, uniquely named staging definition, never uncommitted rows or an advanced checkpoint.

## Consequences

Do not use `OracleBulkCopy`: its work is independent of application transactions and cannot preserve Replicera's atomic checkpoint boundary. Array binding keeps page writes set-based while participating in the active transaction. Integration tests must cover rollback, lock exclusion, supported value families, schema safety, and checkpoint/metric commits against Oracle AI Database Free 26ai.
