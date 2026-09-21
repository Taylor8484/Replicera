using Replicera.Core.Models;

namespace Replicera.Provider.Oracle.Tests;

public sealed class OracleSqlBuilderTests
{
    [Fact]
    public void BuildCreateTable_UsesOracleTypesAndLookupTarget()
    {
        var sql = OracleDdlBuilder.BuildCreateTable(Table());

        Assert.Contains("CREATE TABLE \"ACCOUNT\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"ACCOUNTID\" RAW(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"OWNERID_TYPE\" NVARCHAR2(128) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"ACCOUNTID\")", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplyStaging_UsesMergeAndPhysicalDelete()
    {
        var merge = OracleDmlBuilder.BuildApplyStaging(Table(), "REPLICERA_STAGE_123");
        var delete = OracleDmlBuilder.BuildDeleteStagingChanges(Table(), "REPLICERA_STAGE_123");

        Assert.Contains("MERGE INTO \"ACCOUNT\"", merge, StringComparison.Ordinal);
        Assert.Contains("WHEN MATCHED THEN UPDATE", merge, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", merge, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM \"ACCOUNT\"", delete, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreateStaging_AllowsSparseDeleteRecords()
    {
        var sql = OracleDmlBuilder.BuildCreateStaging(Table(), "REPLICERA_STAGE_123");

        Assert.Contains("\"ACCOUNTID\" RAW(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"OWNERID\" RAW(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"__REPLICERA_OPERATION\" CHAR(1) NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AS SELECT", sql, StringComparison.Ordinal);
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
