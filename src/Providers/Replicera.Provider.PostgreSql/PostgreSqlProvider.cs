using Npgsql;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;

namespace Replicera.Provider.PostgreSql;

/// <summary>Creates PostgreSQL destination components.</summary>
/// <param name="commandTimeout">
/// The configured statement timeout. When null, a timeout already set in the connection string is
/// kept, otherwise <see cref="DestinationConfiguration.DefaultCommandTimeout"/> applies.
/// </param>
public sealed class PostgreSqlProvider(TimeSpan? commandTimeout = null) : IDestinationProvider
{
    public string ProviderName => "postgresql";

    public string DefaultSchema => "public";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new PostgreSqlConnection(Configure(connectionString));

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new PostgreSqlSchemaManager(Configure(connectionString));

    public IDestinationWriter CreateWriter(string connectionString) =>
        new PostgreSqlDestinationWriter(Configure(connectionString));

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new PostgreSqlReplicationStateStore(Configure(connectionString));

    public Task<IDestinationTableLock> AcquireTableLockAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken) =>
        PostgreSqlTableLock.AcquireAsync(Configure(connectionString), jobName, logicalName, cancellationToken);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new PostgreSqlMetadataStore(Configure(connectionString)).EnsureCreatedAsync(cancellationToken);

    public async Task<MetadataStoreState> GetMetadataStoreStateAsync(string connectionString, CancellationToken cancellationToken) =>
        MetadataStoreStates.FromVersion(
            await new PostgreSqlMetadataStore(Configure(connectionString)).GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false),
            PostgreSqlMetadataStore.CurrentSchemaVersion);

    /// <summary>Applies the command timeout to every statement run on connections from this string.</summary>
    public string Configure(string connectionString)
    {
        if (commandTimeout is null && HasExplicitTimeout(connectionString))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            CommandTimeout = CommandTimeouts.ToSeconds(commandTimeout ?? DestinationConfiguration.DefaultCommandTimeout)
        }.ConnectionString;
    }

    private static bool HasExplicitTimeout(string connectionString)
    {
        var keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }.Keys;
        return keys.Cast<string>().Any(key =>
            string.Equals(key.Replace(" ", string.Empty, StringComparison.Ordinal), "CommandTimeout", StringComparison.OrdinalIgnoreCase));
    }
}
