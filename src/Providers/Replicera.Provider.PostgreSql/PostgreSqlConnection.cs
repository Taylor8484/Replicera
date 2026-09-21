using Npgsql;
using Replicera.Core.Abstractions;

namespace Replicera.Provider.PostgreSql;

public sealed class PostgreSqlConnection(string connectionString) : IDestinationConnection
{
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1;", connection);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
