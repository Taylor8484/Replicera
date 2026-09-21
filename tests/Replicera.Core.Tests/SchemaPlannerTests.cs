using Replicera.Core.Configuration;
using Replicera.Core.Models;
using Replicera.Core.Schema;

namespace Replicera.Core.Tests;

public sealed class SchemaPlannerTests
{
    [Fact]
    public void Plan_CreatesMissingTableAutomatically()
    {
        var plan = SchemaPlanner.Plan(SourceTable(), null, new SchemaPolicy());

        var change = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.CreateTable, change.Kind);
        Assert.True(change.IsAutomatic);
        Assert.False(change.IsBlocking);
    }

    [Fact]
    public void Plan_RefusesUnmanagedMatchingTable()
    {
        var destination = new DestinationTable("dbo", "account", [], false);

        var plan = SchemaPlanner.Plan(SourceTable(), destination, new SchemaPolicy());

        Assert.True(plan.HasBlockingChanges);
        Assert.Equal(SchemaChangeKind.OwnershipConflict, Assert.Single(plan.Changes).Kind);
    }

    [Fact]
    public void Plan_AddsAndExpandsColumnsButRetainsRemovedColumns()
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [
                new("accountid", SourceType.Guid, false),
                new("name", SourceType.String, true, 50),
                new("legacy", SourceType.String, true, 10)
            ],
            true);

        var plan = SchemaPlanner.Plan(SourceTable(includeNumber: true), destination, new SchemaPolicy());

        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn && change.ObjectName == "name");
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn && change.ObjectName == "number");
        var removed = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.SourceColumnRemoved);
        Assert.False(removed.IsAutomatic);
        Assert.False(removed.IsBlocking);
    }

    [Fact]
    public void Plan_BlocksRequiredColumnWithoutBackfill()
    {
        var source = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = "requiredcode", SourceType = SourceType.String, IsNullable = false, MaxLength = 10 }
            ]);
        var destination = new DestinationTable(
            "dbo",
            "account",
            [new DestinationColumn("accountid", SourceType.Guid, false)],
            true);

        var plan = SchemaPlanner.Plan(source, destination, new SchemaPolicy());

        var change = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.IncompatibleColumn, change.Kind);
        Assert.True(change.IsBlocking);
    }

    [Fact]
    public void Plan_IgnoresUnsupportedSourceColumns()
    {
        var source = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "derivedname",
                    SourceType = SourceType.String,
                    UnsupportedReason = "Derived columns cannot be requested directly."
                }
            ]);
        var destination = new DestinationTable(
            "dbo",
            "account",
            [new DestinationColumn("accountid", SourceType.Guid, false)],
            true);

        var plan = SchemaPlanner.Plan(source, destination, new SchemaPolicy());

        Assert.Empty(plan.Changes);
    }

    private static TableDefinition SourceTable(bool includeNumber = false)
    {
        var columns = new List<ColumnDefinition>
        {
            new() { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
            new() { LogicalName = "name", SourceType = SourceType.String, IsNullable = true, MaxLength = 100 }
        };

        if (includeNumber)
        {
            columns.Add(new() { LogicalName = "number", SourceType = SourceType.Int32, IsNullable = true });
        }

        return new TableDefinition("account", "accounts", "account", columns);
    }
}
