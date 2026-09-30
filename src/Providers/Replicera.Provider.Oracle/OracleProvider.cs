using Replicera.Core.Abstractions;

namespace Replicera.Provider.Oracle;

/// <summary>Creates Oracle destination components.</summary>
/// <param name="commandTimeout">
/// The configured statement timeout for writing synchronized rows. When null,
/// <see cref="Core.Configuration.DestinationConfiguration.DefaultCommandTimeout"/> applies.
/// </param>
public sealed class OracleProvider(TimeSpan? commandTimeout = null) : IDestinationProvider
{
    public string ProviderName => "oracle";

    public string DefaultSchema => "current-user";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new OracleConnectionProbe(connectionString);

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new OracleSchemaManager(connectionString);

    public IDestinationWriter CreateWriter(string connectionString) =>
        new OracleDestinationWriter(connectionString, commandTimeout);

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new OracleReplicationStateStore(connectionString);

    public Task<IDestinationTableLock> AcquireTableLockAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken) =>
        OracleTableLock.AcquireAsync(connectionString, jobName, logicalName, cancellationToken);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new OracleMetadataStore(connectionString).EnsureCreatedAsync(cancellationToken);

    public async Task<MetadataStoreState> GetMetadataStoreStateAsync(string connectionString, CancellationToken cancellationToken) =>
        MetadataStoreStates.FromVersion(
            await new OracleMetadataStore(connectionString).GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false),
            OracleMetadataStore.CurrentSchemaVersion);
}
