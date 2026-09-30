using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Configuration;

namespace Replicera.Provider.SqlServer;

/// <param name="commandTimeout">
/// The configured statement timeout. When null, a timeout already set in the connection string is
/// kept, otherwise <see cref="DestinationConfiguration.DefaultCommandTimeout"/> applies.
/// </param>
public sealed class SqlServerProvider(TimeSpan? commandTimeout = null) : IDestinationProvider
{
    public string ProviderName => "sqlserver";

    public string DefaultSchema => "dbo";

    public IDestinationConnection CreateConnection(string connectionString) =>
        new SqlServerConnection(Configure(connectionString));

    public IDestinationSchemaManager CreateSchemaManager(string connectionString) =>
        new SqlServerSchemaManager(Configure(connectionString));

    public IDestinationWriter CreateWriter(string connectionString) =>
        new SqlServerDestinationWriter(Configure(connectionString));

    public IReplicationStateStore CreateStateStore(string connectionString) =>
        new SqlServerReplicationStateStore(Configure(connectionString));

    public Task<IDestinationTableLock> AcquireTableLockAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken) =>
        SqlServerTableLock.AcquireAsync(Configure(connectionString), jobName, logicalName, cancellationToken);

    public Task EnsureMetadataStoreAsync(string connectionString, CancellationToken cancellationToken) =>
        new SqlServerMetadataStore(Configure(connectionString)).EnsureCreatedAsync(cancellationToken);

    /// <summary>Applies the command timeout to every statement run on connections from this string.</summary>
    public string Configure(string connectionString)
    {
        if (commandTimeout is null && HasExplicitTimeout(connectionString))
        {
            return connectionString;
        }

        return new SqlConnectionStringBuilder(connectionString)
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
