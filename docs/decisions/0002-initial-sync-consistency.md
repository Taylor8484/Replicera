# ADR 0002: Initial synchronization through the change feed

- Status: Accepted
- Date: 2026-09-19

## Decision

Use a tokenless `RetrieveEntityChangesRequest` as the initial data stream. Follow its paging cookie until the terminal page, apply every page within one destination transaction, and retain the returned `DataToken` only when that transaction commits. Do not combine an unrelated full-table query with a separately acquired token.

Microsoft documents the tokenless request as the initial synchronization: it returns all current records, pages with the returned cookie, and supplies the version for the next request on the terminal page. The feed and terminal token therefore form the consistency boundary; Replicera does not run a separate full-table query or an additional catch-up query.

## Invariants

- The destination table is not marked initialized until every page is applied.
- A failed attempt rolls back its staging table, target mutations, and checkpoint.
- The terminal data token and target mutations commit in the same destination transaction.
- A token is never logged, parsed, edited, or synthesized.

## Validation

On 2026-09-20, a credentialed Linux test forced a multipage initial feed and performed a create, update, and delete after the first page. Applying the remaining initial pages followed by an immediate incremental read produced the current source ID set without losing the concurrent changes.

## References

- [Use change tracking to synchronize data with external systems](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/use-change-tracking-synchronize-data-external-systems)
