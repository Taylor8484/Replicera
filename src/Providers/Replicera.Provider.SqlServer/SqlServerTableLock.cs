using Microsoft.Data.SqlClient;
using Replicera.Core.Abstractions;
using Replicera.Core.Errors;

namespace Replicera.Provider.SqlServer;

internal sealed class SqlServerTableLock(SqlConnection connection, string resource, string jobName, string logicalName) : IDestinationTableLock
{
    public static async Task<IDestinationTableLock> AcquireAsync(
        string connectionString,
        string jobName,
        string logicalName,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var resource = $"replicera:{jobName}:{logicalName}";
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 0;
                SELECT @result;
                """;
            _ = command.Parameters.AddWithValue("@resource", resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new SynchronizationAlreadyRunningException(jobName, logicalName);
            }

            return new SqlServerTableLock(connection, resource, jobName, logicalName);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        string? mode;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT APPLOCK_MODE('public', @resource, 'Session');";
            _ = command.Parameters.AddWithValue("@resource", resource);
            mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            throw new TableLockLostException(jobName, logicalName, exception);
        }

        if (!string.Equals(mode, "Exclusive", StringComparison.Ordinal))
        {
            throw new TableLockLostException(jobName, logicalName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                _ = command.Parameters.AddWithValue("@resource", resource);
                _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
