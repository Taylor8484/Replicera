using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerDdlBuilderTests
{
    [Fact]
    public void BuildCreateTable_QuotesNamesAndPreservesPolymorphicLookupTarget()
    {
        var table = new TableDefinition(
            "order",
            "orders",
            "order detail",
            [
                new ColumnDefinition
                {
                    LogicalName = "orderid",
                    SourceType = SourceType.Guid,
                    IsPrimaryKey = true
                },
                new ColumnDefinition
                {
                    LogicalName = "ownerid",
                    SourceType = SourceType.Lookup,
                    IsNullable = true,
                    LookupTargets = ["systemuser", "team"]
                }
            ]);

        var sql = SqlServerDdlBuilder.BuildCreateTable(table);

        Assert.Contains("CREATE TABLE [dbo].[order_detail]", sql, StringComparison.Ordinal);
        Assert.Contains("[ownerid] uniqueidentifier NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[ownerid_type] nvarchar(128) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY ([orderid])", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreateTable_OmitsUnsupportedColumn()
    {
        var table = new TableDefinition(
            "annotation",
            "annotations",
            "annotation",
            [
                new ColumnDefinition { LogicalName = "annotationid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "documentbody",
                    SourceType = SourceType.Text,
                    UnsupportedReason = "Binary content is unsupported."
                }
            ]);

        var sql = SqlServerDdlBuilder.BuildCreateTable(table);

        Assert.Contains("[annotationid] uniqueidentifier NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("documentbody", sql, StringComparison.Ordinal);
    }
}
