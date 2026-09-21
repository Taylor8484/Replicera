using Replicera.Core.Abstractions;

namespace Replicera.Provider.Oracle;

public sealed class OracleProvider : IDestinationProvider
{
    public string ProviderName => "oracle";

    public string DefaultSchema => "current-user";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new OracleConnectionProbe(connectionString);

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new OracleSchemaManager(connectionString);

    public IDestinationWriter CreateWriter(string connectionString) =>
        new OracleDestinationWriter(connectionString);

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new OracleReplicationStateStore(connectionString);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new OracleMetadataStore(connectionString).EnsureCreatedAsync(cancellationToken);
}
