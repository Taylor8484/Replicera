using Replicera.Core.Models;

namespace Replicera.Core.Tests;

public sealed class TableDefinitionTests
{
    [Fact]
    public void Constructor_RejectsDuplicateLogicalNamesIgnoringCase()
    {
        var columns = new[]
        {
            PrimaryKey(),
            new ColumnDefinition { LogicalName = "Name", SourceType = SourceType.String },
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String }
        };

        var exception = Assert.Throws<ArgumentException>(
            () => new TableDefinition("account", "accounts", "account", columns));

        Assert.Contains("Duplicate column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RequiresExactlyOnePrimaryKey()
    {
        var columns = new[]
        {
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String }
        };

        var exception = Assert.Throws<ArgumentException>(
            () => new TableDefinition("account", "accounts", "account", columns));

        Assert.Contains("exactly one primary key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ExposesPrimaryKey()
    {
        var table = new TableDefinition("account", "accounts", "account", [PrimaryKey()]);

        Assert.Equal("accountid", table.PrimaryKey.LogicalName);
    }

    private static ColumnDefinition PrimaryKey() => new()
    {
        LogicalName = "accountid",
        SourceType = SourceType.Guid,
        IsPrimaryKey = true
    };
}
