using Oracle.ManagedDataAccess.Client;
using Replicera.Core.Models;

namespace Replicera.Provider.Oracle;

public sealed record OracleType(string Declaration, OracleDbType DbType);

public static class OracleTypeMapper
{
    public static OracleType Map(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.SourceType switch
        {
            SourceType.Guid or SourceType.Lookup => new("RAW(16)", OracleDbType.Raw),
            SourceType.String => MapString(column.MaxLength),
            SourceType.Text or SourceType.MultiSelectChoice => new("NCLOB", OracleDbType.NClob),
            SourceType.Boolean => new("NUMBER(1)", OracleDbType.Int16),
            SourceType.Int32 or SourceType.Choice => new("NUMBER(10)", OracleDbType.Int32),
            SourceType.Int64 => new("NUMBER(19)", OracleDbType.Int64),
            SourceType.Decimal => new(MapDecimal(column, 18, 2), OracleDbType.Decimal),
            SourceType.Money => new(MapDecimal(column, 19, 4), OracleDbType.Decimal),
            SourceType.Double => new("BINARY_DOUBLE", OracleDbType.BinaryDouble),
            SourceType.DateTime when column.DateTimeBehavior == DateTimeBehavior.DateOnly => new("DATE", OracleDbType.Date),
            SourceType.DateTime when column.DateTimeBehavior == DateTimeBehavior.UserLocal =>
                new("TIMESTAMP(7) WITH TIME ZONE", OracleDbType.TimeStampTZ),
            SourceType.DateTime => new("TIMESTAMP(7)", OracleDbType.TimeStamp),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.SourceType, "Unsupported source type.")
        };
    }

    private static OracleType MapString(int? maxLength)
    {
        if (maxLength is null or > 2_000)
        {
            return new("NCLOB", OracleDbType.NClob);
        }

        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "String length must be positive.");
        }

        return new($"NVARCHAR2({maxLength.Value})", OracleDbType.NVarchar2);
    }

    private static string MapDecimal(ColumnDefinition column, int defaultPrecision, int defaultScale)
    {
        var precision = column.Precision ?? defaultPrecision;
        var scale = column.Scale ?? defaultScale;
        if (precision is < 1 or > 38 || scale < 0 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(column), $"Invalid NUMBER precision/scale {precision},{scale} for '{column.LogicalName}'.");
        }

        return $"NUMBER({precision},{scale})";
    }
}
