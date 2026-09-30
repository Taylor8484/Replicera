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

    [Theory]
    [InlineData("nvarchar", 40, 0, 0, "nvarchar(20)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 10, 0, 0, "varchar(10)")]
    [InlineData("decimal", 17, 38, 4, "decimal(38,4)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    [InlineData("int", 4, 10, 0, "int")]
    public void FormatDeclaration_RestatesExistingColumnType(string typeName, short maxLength, byte precision, byte scale, string expected)
    {
        Assert.Equal(expected, SqlServerSchemaManager.FormatDeclaration(typeName, maxLength, precision, scale));
    }

    [Fact]
    public void BuildRelaxNullability_UsesExistingTypeForColumnOutsideSourceLayout()
    {
        Assert.Equal(
            "ALTER TABLE [dbo].[account] ALTER COLUMN [legacy] nvarchar(20) NULL;",
            SqlServerSchemaManager.BuildRelaxNullability(RequiredColumnTable(), "legacy", "nvarchar(20)"));
    }

    [Fact]
    public void BuildReplaceColumn_DropsOrRenamesThenAddsColumn()
    {
        Assert.Equal(
            ["ALTER TABLE [dbo].[account] DROP COLUMN [name];", "ALTER TABLE [dbo].[account] ADD [name] nvarchar(100) NULL;"],
            SqlServerSchemaManager.BuildReplaceColumn(RequiredColumnTable(), "name", null));
        var preserved = SqlServerSchemaManager.BuildReplaceColumn(RequiredColumnTable(), "name", "name_replaced_20260930");
        Assert.Equal("EXEC sys.sp_rename N'[dbo].[account].[name]', N'name_replaced_20260930', N'COLUMN';", preserved[0]);
        Assert.Equal("ALTER TABLE [dbo].[account] ADD [name] nvarchar(100) NULL;", preserved[1]);
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
