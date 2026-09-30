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

    [Fact]
    public void BuildCreateTable_AllowsNullsInRequiredSourceColumns()
    {
        var sql = SqlServerDdlBuilder.BuildCreateTable(RequiredColumnTable());

        Assert.Contains("[accountid] uniqueidentifier NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[name] nvarchar(100) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[ownerid] uniqueidentifier NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[ownerid_type] nvarchar(128) NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[name] nvarchar(100) NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRelaxNullability_KeepsColumnTypeAndAllowsNulls()
    {
        Assert.Equal(
            "ALTER TABLE [dbo].[account] ALTER COLUMN [name] nvarchar(100) NULL;",
            SqlServerSchemaManager.BuildRelaxNullability(RequiredColumnTable(), "name"));
        Assert.Equal(
            "ALTER TABLE [dbo].[account] ALTER COLUMN [ownerid_type] nvarchar(128) NULL;",
            SqlServerSchemaManager.BuildRelaxNullability(RequiredColumnTable(), "ownerid_type"));
    }

    private static TableDefinition RequiredColumnTable() => new(
        "account",
        "accounts",
        "account",
        [
            new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = false, MaxLength = 100 },
            new ColumnDefinition
            {
                LogicalName = "ownerid",
                SourceType = SourceType.Lookup,
                IsNullable = false,
                LookupTargets = ["systemuser", "team"]
            }
        ]);
}
