using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql.Tests;

public sealed class PostgreSqlSqlBuilderTests
{
    [Fact]
    public void BuildCreateTable_UsesNativeTypesAndLookupTargetColumn()
    {
        var sql = PostgreSqlDdlBuilder.BuildCreateTable(Table());

        Assert.Contains("CREATE TABLE \"public\".\"account\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"accountid\" uuid NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"ownerid_type\" character varying(128) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"accountid\")", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplyStaging_UsesConflictUpsertAndPhysicalDelete()
    {
        var sql = PostgreSqlDmlBuilder.BuildApplyStaging(Table(), "replicera_stage_123");

        Assert.Contains("ON CONFLICT (\"accountid\") DO UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM \"public\".\"account\"", sql, StringComparison.Ordinal);
        Assert.Contains("USING \"replicera_stage_123\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplyStaging_NoDataLossStampsLoadsAndSourceRemovals()
    {
        var sql = PostgreSqlDmlBuilder.BuildApplyStaging(
            Table(),
            "replicera_stage_123",
            retainDeletedRows: true);

        Assert.Contains("\"data_load_dte\" = CURRENT_TIMESTAMP", sql, StringComparison.Ordinal);
        Assert.Contains("\"date_source_remove_dte\" = NULL", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(target.\"date_source_remove_dte\", CURRENT_TIMESTAMP)", sql, StringComparison.Ordinal);
    }

    private static TableDefinition Table() => new(
        "account",
        "accounts",
        "account",
        [
            new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition
            {
                LogicalName = "ownerid",
                SourceType = SourceType.Lookup,
                IsNullable = true,
                LookupTargets = ["systemuser", "team"]
            }
        ]);
}
