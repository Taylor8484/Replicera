using NpgsqlTypes;
using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql;

public sealed record PostgreSqlType(string Declaration, NpgsqlDbType DbType);

public static class PostgreSqlTypeMapper
{
    public static PostgreSqlType Map(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.SourceType switch
        {
            SourceType.Guid or SourceType.Lookup => new("uuid", NpgsqlDbType.Uuid),
            SourceType.String => MapString(column.MaxLength),
            SourceType.Text or SourceType.MultiSelectChoice => new("text", NpgsqlDbType.Text),
            SourceType.Boolean => new("boolean", NpgsqlDbType.Boolean),
            SourceType.Int32 or SourceType.Choice => new("integer", NpgsqlDbType.Integer),
            SourceType.Int64 => new("bigint", NpgsqlDbType.Bigint),
            SourceType.Decimal => new(MapDecimal(column, 18, 2), NpgsqlDbType.Numeric),
            SourceType.Money => new(MapDecimal(column, 19, 4), NpgsqlDbType.Numeric),
            SourceType.Double => new("double precision", NpgsqlDbType.Double),
            SourceType.DateTime when column.DateTimeBehavior == DateTimeBehavior.DateOnly => new("date", NpgsqlDbType.Date),
            SourceType.DateTime when column.DateTimeBehavior == DateTimeBehavior.TimeZoneIndependent =>
                new("timestamp without time zone", NpgsqlDbType.Timestamp),
            SourceType.DateTime => new("timestamp with time zone", NpgsqlDbType.TimestampTz),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.SourceType, "Unsupported source type.")
        };
    }

    private static PostgreSqlType MapString(int? maxLength)
    {
        if (maxLength is null)
        {
            return new("text", NpgsqlDbType.Text);
        }

        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "String length must be positive.");
        }

        return new($"character varying({maxLength.Value})", NpgsqlDbType.Varchar);
    }

    private static string MapDecimal(ColumnDefinition column, int defaultPrecision, int defaultScale)
    {
        var precision = column.Precision ?? defaultPrecision;
        var scale = column.Scale ?? defaultScale;
        if (precision is < 1 or > 1000 || scale < 0 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(column), $"Invalid numeric precision/scale {precision},{scale} for '{column.LogicalName}'.");
        }

        return $"numeric({precision},{scale})";
    }
}
