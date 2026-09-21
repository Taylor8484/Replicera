using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer;

public sealed record SqlServerType(string Declaration, Type ClrType);

public static class SqlServerTypeMapper
{
    public static SqlServerType Map(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return column.SourceType switch
        {
            SourceType.Guid or SourceType.Lookup => new("uniqueidentifier", typeof(Guid)),
            SourceType.String => new(MapString(column.MaxLength), typeof(string)),
            SourceType.Text or SourceType.MultiSelectChoice => new("nvarchar(max)", typeof(string)),
            SourceType.Boolean => new("bit", typeof(bool)),
            SourceType.Int32 or SourceType.Choice => new("int", typeof(int)),
            SourceType.Int64 => new("bigint", typeof(long)),
            SourceType.Decimal => new(MapDecimal(column, 18, 2), typeof(decimal)),
            SourceType.Money => new(MapDecimal(column, 19, 4), typeof(decimal)),
            SourceType.Double => new("float(53)", typeof(double)),
            SourceType.DateTime when column.DateTimeBehavior == DateTimeBehavior.DateOnly => new("date", typeof(DateOnly)),
            SourceType.DateTime => new("datetime2(7)", typeof(DateTime)),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.SourceType, "Unsupported source type.")
        };
    }

    private static string MapString(int? maxLength)
    {
        if (maxLength is null or > 4_000)
        {
            return "nvarchar(max)";
        }

        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "String length must be positive.");
        }

        return $"nvarchar({maxLength.Value})";
    }

    private static string MapDecimal(ColumnDefinition column, int defaultPrecision, int defaultScale)
    {
        var precision = column.Precision ?? defaultPrecision;
        var scale = column.Scale ?? defaultScale;
        if (precision is < 1 or > 38 || scale < 0 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(column),
                $"Invalid decimal precision/scale {precision},{scale} for '{column.LogicalName}'.");
        }

        return $"decimal({precision},{scale})";
    }
}
