using System.Data;
using System.Text.Json;
using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public static class SqlServerBatchTable
{
    public static DataTable Create(TableDefinition table, SourcePage page)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(page);
        var layout = SqlServerTableLayout.GetColumns(table);
        var data = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        foreach (var physicalColumn in layout)
        {
            var clrType = physicalColumn.IsLookupTarget
                ? typeof(string)
                : SqlServerTypeMapper.Map(physicalColumn.Source).ClrType;
            _ = data.Columns.Add(physicalColumn.Name, clrType);
        }

        _ = data.Columns.Add(SqlServerDmlBuilder.OperationColumn, typeof(string));
        foreach (var record in page.Records)
        {
            var row = data.NewRow();
            foreach (var physicalColumn in layout)
            {
                row[physicalColumn.Name] = GetValue(physicalColumn, record) ?? DBNull.Value;
            }

            row[SqlServerDmlBuilder.OperationColumn] = record.Kind == ChangeKind.Delete ? "D" : "U";
            data.Rows.Add(row);
        }

        return data;
    }

    private static object? GetValue(
        SqlServerPhysicalColumn physicalColumn,
        SourceRecord record)
    {
        if (physicalColumn.Source.IsPrimaryKey)
        {
            return record.Id;
        }

        if (record.Kind == ChangeKind.Delete
            || !record.Values.TryGetValue(physicalColumn.Source.LogicalName, out var value)
            || value is null)
        {
            return null;
        }

        if (physicalColumn.IsLookupTarget)
        {
            return value is LookupValue lookup ? lookup.TargetLogicalName : null;
        }

        return value switch
        {
            LookupValue lookup => lookup.Id,
            ChoiceSetValue choices => JsonSerializer.Serialize(choices.Values),
            DateTime dateTime when physicalColumn.Source.SourceType == SourceType.DateTime
                                   && physicalColumn.Source.DateTimeBehavior == DateTimeBehavior.DateOnly =>
                DateOnly.FromDateTime(dateTime),
            DateOnly date => date,
            _ => value
        };
    }
}
