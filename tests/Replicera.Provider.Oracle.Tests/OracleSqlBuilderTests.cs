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
    public void BuildCreateTable_AllowsNullsInRequiredSourceColumns()
    {
        var table = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = false, MaxLength = 100 }
            ]);

        var sql = OracleDdlBuilder.BuildCreateTable(table);

        Assert.Contains("\"ACCOUNTID\" RAW(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"NAME\" NVARCHAR2(100) NULL", sql, StringComparison.Ordinal);
        Assert.Equal(
            "ALTER TABLE \"ACCOUNT\" MODIFY (\"NAME\" NULL)",
            OracleSchemaManager.BuildRelaxNullability(table, "name"));
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
    public void BuildApplyStaging_NoDataLossStampsLoadsAndSourceRemovals()
    {
        var merge = OracleDmlBuilder.BuildApplyStaging(Table(), "REPLICERA_STAGE_123", retainDeletedRows: true);
        var removal = OracleDmlBuilder.BuildDeleteStagingChanges(Table(), "REPLICERA_STAGE_123", retainDeletedRows: true);

        Assert.Contains("\"DATA_LOAD_DTE\" = SYSTIMESTAMP", merge, StringComparison.Ordinal);
        Assert.Contains("\"DATE_SOURCE_REMOVE_DTE\" = NULL", merge, StringComparison.Ordinal);
        Assert.Contains("COALESCE(target.\"DATE_SOURCE_REMOVE_DTE\", SYSTIMESTAMP)", removal, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMarkAllSourceRemoved_UsesNormalizedManagedColumn()
    {
        var sql = OracleDmlBuilder.BuildMarkAllSourceRemoved(Table());

        Assert.Equal(
            "UPDATE \"ACCOUNT\" SET \"DATE_SOURCE_REMOVE_DTE\" = COALESCE(\"DATE_SOURCE_REMOVE_DTE\", SYSTIMESTAMP)",
            sql);
    }

    [Fact]
    public void BuildExpandColumn_ModifiesCharacterColumnWithinNvarchar2Limit()
    {
        var statement = Assert.Single(OracleSchemaManager.BuildExpandColumn(TextTable(2_000), "name"));

        Assert.Equal("ALTER TABLE \"ACCOUNT\" MODIFY (\"NAME\" NVARCHAR2(2000))", statement);
    }

    [Fact]
    public void BuildExpandColumn_CopiesIntoNclobWhenCrossingNvarchar2Limit()
    {
        var statements = OracleSchemaManager.BuildExpandColumn(TextTable(4_000), "name");

        Assert.Equal(5, statements.Count);
        Assert.Contains("DROP COLUMN \"NAME_REPLICERA_COPY\"", statements[0], StringComparison.Ordinal);
        Assert.Contains("SQLCODE <> -904", statements[0], StringComparison.Ordinal);
        Assert.Equal("ALTER TABLE \"ACCOUNT\" ADD (\"NAME_REPLICERA_COPY\" NCLOB NULL)", statements[1]);
        Assert.Equal("UPDATE \"ACCOUNT\" SET \"NAME_REPLICERA_COPY\" = \"NAME\"", statements[2]);
        Assert.Equal("ALTER TABLE \"ACCOUNT\" DROP COLUMN \"NAME\"", statements[3]);
        Assert.Equal("ALTER TABLE \"ACCOUNT\" RENAME COLUMN \"NAME_REPLICERA_COPY\" TO \"NAME\"", statements[4]);
        Assert.DoesNotContain(statements, statement => statement.Contains("MODIFY", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildExpandColumn_CopiesNumericColumnsBecauseOracleRejectsScaleChangesOnData()
    {
        var table = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "amount", SourceType = SourceType.Decimal, IsNullable = true, Precision = 38, Scale = 4 }
            ]);

        var statements = OracleSchemaManager.BuildExpandColumn(table, "amount");

        Assert.Equal(5, statements.Count);
        Assert.Equal("ALTER TABLE \"ACCOUNT\" ADD (\"AMOUNT_REPLICERA_COPY\" NUMBER(38,4) NULL)", statements[1]);
        Assert.Equal("UPDATE \"ACCOUNT\" SET \"AMOUNT_REPLICERA_COPY\" = \"AMOUNT\"", statements[2]);
    }

    private static TableDefinition TextTable(int maxLength) => new(
        "account",
        "accounts",
        "account",
        [
            new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = maxLength }
        ]);

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
