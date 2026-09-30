using Npgsql;

namespace Replicera.Provider.PostgreSql.Tests;

public sealed class PostgreSqlProviderTests
{
    private const string ConnectionString = "Host=localhost;Database=replicera;Username=replicera";

    [Fact]
    public void Configure_AppliesDefaultTimeoutWhenNoneIsSet()
    {
        var configured = new PostgreSqlProvider().Configure(ConnectionString);

        Assert.Equal(600, new NpgsqlConnectionStringBuilder(configured).CommandTimeout);
    }

    [Theory]
    [InlineData("Command Timeout=45")]
    [InlineData("CommandTimeout=45")]
    public void Configure_KeepsConnectionStringTimeoutWhenNotConfigured(string setting)
    {
        var configured = new PostgreSqlProvider().Configure($"{ConnectionString};{setting}");

        Assert.Equal(45, new NpgsqlConnectionStringBuilder(configured).CommandTimeout);
    }

    [Fact]
    public void Configure_ConfiguredTimeoutOverridesConnectionString()
    {
        var configured = new PostgreSqlProvider(TimeSpan.FromMinutes(2)).Configure($"{ConnectionString};Command Timeout=45");

        Assert.Equal(120, new NpgsqlConnectionStringBuilder(configured).CommandTimeout);
    }
}
