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

        Assert.Equal(2, plan.Changes.Count);
        var change = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.CreateTable);
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

        var plan = SchemaPlanner.Plan(
            SourceTable(includeNumber: true),
            destination,
            new SchemaPolicy(),
            SynchronizationMode.NoDataLoss);

        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.ExpandColumn && change.ObjectName == "name");
        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn && change.ObjectName == "number");
        var removed = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.SourceColumnRemoved);
        Assert.False(removed.IsAutomatic);
        Assert.False(removed.IsBlocking);
    }

    [Fact]
    public void Plan_AddsRequiredColumnForNullableDestinationBackfill()
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

        var change = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.AddColumn);
        Assert.Equal(SchemaChangeKind.AddColumn, change.Kind);
        Assert.True(change.IsAutomatic);
        Assert.False(change.IsBlocking);
    }

    [Fact]
    public void Plan_CompleteModeDropsRemovedColumns()
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [
                new("accountid", SourceType.Guid, false),
                new("name", SourceType.String, true, 100),
                new("legacy", SourceType.String, true, 10)
            ],
            true);

        var plan = SchemaPlanner.Plan(SourceTable(), destination, new SchemaPolicy());

        var removed = Assert.Single(plan.Changes, change => change.Kind == SchemaChangeKind.DropColumn);
        Assert.Equal(SchemaChangeKind.DropColumn, removed.Kind);
        Assert.True(removed.IsAutomatic);
        Assert.False(removed.IsBlocking);
    }

    [Fact]
    public void Plan_TreatsPolymorphicLookupTypeAsPartOfSourceLayout()
    {
        var source = new TableDefinition(
            "activity",
            "activities",
            "activity",
            [
                new ColumnDefinition { LogicalName = "activityid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "regardingid",
                    SourceType = SourceType.Lookup,
                    IsNullable = true,
                    LookupTargets = ["account", "contact"]
                }
            ]);
        var destination = new DestinationTable(
            "dbo",
            "activity",
            [
                new("activityid", SourceType.Guid, false),
                new("regardingid", SourceType.Lookup, true),
                new("regardingid_type", SourceType.String, true),
                new(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true)
            ],
            true);

        var plan = SchemaPlanner.Plan(source, destination, new SchemaPolicy());

        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Plan_RepairsMissingPolymorphicLookupTypeColumn()
    {
        var source = new TableDefinition(
            "activity",
            "activities",
            "activity",
            [
                new ColumnDefinition { LogicalName = "activityid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition
                {
                    LogicalName = "regardingid",
                    SourceType = SourceType.Lookup,
                    IsNullable = true,
                    LookupTargets = ["account", "contact"]
                }
            ]);
        var destination = new DestinationTable(
            "dbo",
            "activity",
            [
                new("activityid", SourceType.Guid, false),
                new("regardingid", SourceType.Lookup, true),
                new(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true)
            ],
            true);

        var plan = SchemaPlanner.Plan(source, destination, new SchemaPolicy());

        var repair = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.AddLookupTypeColumn, repair.Kind);
        Assert.Equal("regardingid", repair.ObjectName);
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
            [
                new DestinationColumn("accountid", SourceType.Guid, false),
                new DestinationColumn(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true)
            ],
            true);

        var plan = SchemaPlanner.Plan(source, destination, new SchemaPolicy());

        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Plan_ReloadRecreatesTableAndManagedColumns()
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [new("accountid", SourceType.Guid, false)],
            true);

        var plan = SchemaPlanner.Plan(SourceTable(), destination, new SchemaPolicy(), SynchronizationMode.Reload);

        Assert.Contains(plan.Changes, change => change.Kind == SchemaChangeKind.RecreateTable);
        Assert.Contains(plan.Changes, change =>
            change.Kind == SchemaChangeKind.AddManagedColumn
            && change.ObjectName == ManagedColumnNames.DataLoadDate);
    }

    [Fact]
    public void Plan_NoDataLossAddsSourceRemovalDate()
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [
                new("accountid", SourceType.Guid, false),
                new("name", SourceType.String, true, 100),
                new(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true)
            ],
            true);

        var plan = SchemaPlanner.Plan(SourceTable(), destination, new SchemaPolicy(), SynchronizationMode.NoDataLoss);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.AddManagedColumn, change.Kind);
        Assert.Equal(ManagedColumnNames.SourceRemoveDate, change.ObjectName);
    }

    [Fact]
    public void Plan_BlocksSourceCollisionWithManagedColumn()
    {
        var source = new TableDefinition(
            "account",
            "accounts",
            "account",
            [
                new ColumnDefinition { LogicalName = "accountid", SourceType = SourceType.Guid, IsPrimaryKey = true },
                new ColumnDefinition { LogicalName = ManagedColumnNames.DataLoadDate, SourceType = SourceType.DateTime }
            ]);

        var plan = SchemaPlanner.Plan(source, null, new SchemaPolicy());

        Assert.True(plan.HasBlockingChanges);
        Assert.Equal(SchemaChangeKind.IncompatibleColumn, Assert.Single(plan.Changes).Kind);
    }

    [Theory]
    [InlineData(SynchronizationMode.Complete)]
    [InlineData(SynchronizationMode.NoDataLoss)]
    public void Plan_AppliesExplicitColumnRenameWithoutDropOrAdd(SynchronizationMode mode)
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [
                new("accountid", SourceType.Guid, false),
                new("old_name", SourceType.String, true, 100),
                new(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true),
                .. mode == SynchronizationMode.NoDataLoss
                    ? new[] { new DestinationColumn(ManagedColumnNames.SourceRemoveDate, SourceType.DateTime, true) }
                    : Array.Empty<DestinationColumn>()
            ],
            true);
        var policy = new SchemaPolicy
        {
            ColumnRenames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["account"] = new Dictionary<string, string> { ["old_name"] = "name" }
            }
        };

        var plan = SchemaPlanner.Plan(SourceTable(), destination, policy, mode);

        var rename = Assert.Single(plan.Changes);
        Assert.Equal(SchemaChangeKind.RenameColumn, rename.Kind);
        Assert.Equal("old_name", rename.ObjectName);
        Assert.Equal("name", rename.NewObjectName);
    }

    [Fact]
    public void Plan_TreatsAlreadyAppliedRenameAsIdempotent()
    {
        var destination = new DestinationTable(
            "dbo",
            "account",
            [
                new("accountid", SourceType.Guid, false),
                new("name", SourceType.String, true, 100),
                new(ManagedColumnNames.DataLoadDate, SourceType.DateTime, true)
            ],
            true);
        var policy = new SchemaPolicy
        {
            ColumnRenames = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["account"] = new Dictionary<string, string> { ["old_name"] = "name" }
            }
        };

        Assert.Empty(SchemaPlanner.Plan(SourceTable(), destination, policy).Changes);
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
