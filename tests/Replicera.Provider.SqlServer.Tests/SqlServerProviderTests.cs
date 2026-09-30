using Microsoft.Data.SqlClient;

namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerProviderTests
{
    private const string ConnectionString = "Server=localhost;Database=replicera;Integrated Security=true";

    [Fact]
    public void Configure_AppliesDefaultTimeoutWhenNoneIsSet()
    {
        var configured = new SqlServerProvider().Configure(ConnectionString);

        Assert.Equal(600, new SqlConnectionStringBuilder(configured).CommandTimeout);
    }

    [Fact]
    public void Configure_KeepsConnectionStringTimeoutWhenNotConfigured()
    {
        var configured = new SqlServerProvider().Configure($"{ConnectionString};Command Timeout=45");

        Assert.Equal(45, new SqlConnectionStringBuilder(configured).CommandTimeout);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    public void Configure_ConfiguredTimeoutOverridesConnectionString(int seconds, int expected)
    {
        var configured = new SqlServerProvider(TimeSpan.FromSeconds(seconds)).Configure($"{ConnectionString};Command Timeout=45");

        Assert.Equal(expected, new SqlConnectionStringBuilder(configured).CommandTimeout);
    }
}
