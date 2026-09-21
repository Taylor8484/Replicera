namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerMetadataStoreTests
{
    [Fact]
    public void MigrationOne_CreatesRequiredMetadataAndConstraints()
    {
        var sql = SqlServerMetadataStore.MigrationOne;

        Assert.Contains("[replicera].[Tables]", sql, StringComparison.Ordinal);
        Assert.Contains("[replicera].[SyncRuns]", sql, StringComparison.Ordinal);
        Assert.Contains("[replicera].[SchemaHistory]", sql, StringComparison.Ordinal);
        Assert.Contains("[ChangeCheckpoint] nvarchar(max)", sql, StringComparison.Ordinal);
        Assert.Contains("UQ_replicera_Tables_Job_Table", sql, StringComparison.Ordinal);
    }
}
