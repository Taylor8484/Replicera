using Replicera.Core.Abstractions;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlProvider : IDestinationProvider
{
    public string ProviderName => "postgresql";

    public string DefaultSchema => "public";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new PostgreSqlConnection(connectionString);

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new PostgreSqlSchemaManager(connectionString);

    public IDestinationWriter CreateWriter(string connectionString) =>
        new PostgreSqlDestinationWriter(connectionString);

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new PostgreSqlReplicationStateStore(connectionString);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new PostgreSqlMetadataStore(connectionString).EnsureCreatedAsync(cancellationToken);
}
