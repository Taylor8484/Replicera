using Replicera.Core.Abstractions;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerProvider : IDestinationProvider
{
    public string ProviderName => "sqlserver";

    public string DefaultSchema => "dbo";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new SqlServerConnection(connectionString);

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new SqlServerSchemaManager(connectionString);

    public IDestinationWriter CreateWriter(string connectionString) =>
        new SqlServerDestinationWriter(connectionString);

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new SqlServerReplicationStateStore(connectionString);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new SqlServerMetadataStore(connectionString).EnsureCreatedAsync(cancellationToken);
}
