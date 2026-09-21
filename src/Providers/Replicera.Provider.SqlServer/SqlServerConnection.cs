using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;

namespace Replicera.Provider.SqlServer;

public sealed class SqlServerConnection(string connectionString) : IDestinationConnection
{
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
