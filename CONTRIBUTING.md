# Contributing

Read the README, operations guide, and relevant architecture decisions before changing code. Keep core replication and schema models independent of Dataverse SDK and destination-provider types.

Create focused changes with tests for observable behavior. Run `dotnet build Replicera.sln` and `dotnet test Replicera.sln` before opening a pull request. Integration tests that require Dataverse, SQL Server, PostgreSQL, or Oracle must be marked and documented so the normal unit-test suite remains deterministic.

Use short imperative commit subjects. Pull requests should reference the relevant specification section, describe failure/recovery behavior where applicable, and report the commands used for validation. Never include credentials, connection strings, access tokens, Dataverse change tokens, or replicated row values in fixtures, logs, issues, or pull requests.
