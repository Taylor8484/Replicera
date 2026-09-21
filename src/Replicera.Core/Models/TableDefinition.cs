namespace Replicera.Core.Models;

public sealed class TableDefinition
{
    public TableDefinition(
        string logicalName,
        string entitySetName,
        string destinationName,
        IEnumerable<ColumnDefinition> columns)
    {
        LogicalName = RequireName(logicalName, nameof(logicalName));
        EntitySetName = RequireName(entitySetName, nameof(entitySetName));
        DestinationName = RequireName(destinationName, nameof(destinationName));
        Columns = columns?.ToArray() ?? throw new ArgumentNullException(nameof(columns));

        if (Columns.Count == 0)
        {
            throw new ArgumentException("A table must contain at least one column.", nameof(columns));
        }

        var duplicate = Columns
            .GroupBy(column => column.LogicalName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate column logical name '{duplicate.Key}'.", nameof(columns));
        }

        var primaryKeys = Columns.Where(column => column.IsPrimaryKey).ToArray();
        if (primaryKeys.Length != 1)
        {
            throw new ArgumentException("A table must contain exactly one primary key column.", nameof(columns));
        }

        PrimaryKey = primaryKeys[0];
    }

    public string LogicalName { get; }

    public string EntitySetName { get; }

    public string DestinationName { get; }

    public ColumnDefinition PrimaryKey { get; }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    private static string RequireName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty name is required.", parameterName);
        }

        return value;
    }
}
