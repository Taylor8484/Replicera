using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Abstractions;

namespace Replicera.Provider.Oracle;

public sealed class OracleConnectionProbe(string connectionString) : IDestinationConnection
{
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DUAL";
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
