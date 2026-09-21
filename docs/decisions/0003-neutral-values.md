# ADR 0003: Neutral schema and value representation

- Status: Accepted for initial implementation
- Date: 2026-09-19

## Decision

Core schema uses semantic source types rather than SQL names. Decimal and money carry precision and scale. Date/time columns carry Dataverse behavior (`UserLocal`, `DateOnly`, or `TimeZoneIndependent`). Choice values retain integer source values; multi-select choices are represented as ordered, distinct integer values at the adapter boundary and serialized by providers according to their documented mapping. Lookups retain the referenced GUID and, for polymorphic lookups, the target logical name.

Calculated and rollup flags remain metadata rather than distinct value types. Unsupported values stop the affected table or appear explicitly in inspection; they are never silently coerced.

## Consequences

Providers must document reversible mappings and validate their length and precision limits. The SQL Server provider will use a stable JSON representation for multi-select values until a normalized enriched mode is designed.
