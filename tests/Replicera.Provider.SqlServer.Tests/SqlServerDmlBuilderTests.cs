using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerDmlBuilderTests
{
    [Fact]
    public void BuildCreateStaging_MakesNonKeyColumnsNullableForDeleteRecords()
    {
        var table = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, MaxLength = 100 }
            ]);

        var sql = SqlServerDmlBuilder.BuildCreateStaging(table, "stage");

        Assert.Contains("ALTER TABLE [dbo].[stage] ALTER COLUMN [name] nvarchar(100) NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN [accountid]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplyStaging_UsesExplicitSetBasedStatements()
    {
        var sql = SqlServerDmlBuilder.BuildApplyStaging(Table(), "replicera_stage_123");

        Assert.Contains("UPDATE target", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [dbo].[account]", sql, StringComparison.Ordinal);
        Assert.Contains("DELETE target", sql, StringComparison.Ordinal);
        Assert.Contains("[__replicera_operation] = 'D'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBatch_PreservesLookupTargetAndStableChoiceJson()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var page = new SourcePage(
        [
            new SourceRecord(
                id,
                ChangeKind.Upsert,
                new Dictionary<string, object?>
                {
                    ["ownerid"] = new LookupValue(owner, "team"),
                    ["categories"] = new ChoiceSetValue([3, 7])
                })
        ],
        null,
        "token",
        false);

        var data = SqlServerBatchTable.Create(Table(), page);

        var row = Assert.Single(data.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(id, row["accountid"]);
        Assert.Equal(owner, row["ownerid"]);
        Assert.Equal("team", row["ownerid_type"]);
        Assert.Equal("[3,7]", row["categories"]);
        Assert.Equal("U", row[SqlServerDmlBuilder.OperationColumn]);
    }

    [Fact]
    public void CreateBatch_ConvertsDataverseDateTimeToDateOnly()
    {
        var table = new TableDefinition(
            "sample",
            "samples",
            "sample",
            [
                new ColumnDefinition { LogicalName = "sampleid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "sampledate",
                    SourceType = SourceType.DateTime,
                    DateTimeBehavior = DateTimeBehavior.DateOnly,
                    IsNullable = true
                }
            ]);
        var page = new SourcePage(
            [
                new SourceRecord(
                    Guid.NewGuid(),
                    ChangeKind.Upsert,
                    new Dictionary<string, object?> { ["sampledate"] = new DateTime(2026, 9, 19, 15, 30, 0, DateTimeKind.Utc) })
            ],
            null,
            "token",
            false);

        var data = SqlServerBatchTable.Create(table, page);

        var row = Assert.Single(data.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(new DateOnly(2026, 9, 19), row["sampledate"]);
    }

    [Fact]
    public void CreateBatch_OmitsUnsupportedColumnsAndValues()
    {
        var table = new TableDefinition(
            "sample",
            "samples",
            "sample",
            [
                new ColumnDefinition { LogicalName = "sampleid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "derivedname",
                    SourceType = SourceType.String,
                    UnsupportedReason = "Derived columns cannot be requested directly."
                }
            ]);
        var page = new SourcePage(
            [
                new SourceRecord(
                    Guid.NewGuid(),
                    ChangeKind.Upsert,
                    new Dictionary<string, object?> { ["derivedname"] = "ignored" })
            ],
            null,
            "token",
            false);

        var data = SqlServerBatchTable.Create(table, page);

        Assert.False(data.Columns.Contains("derivedname"));
        Assert.Single(data.Rows);
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
            },
            new ColumnDefinition { LogicalName = "categories", SourceType = SourceType.MultiSelectChoice, IsNullable = true }
        ]);
}
