using Replicera.Core.Models;

namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerTypeMapperTests
{
    [Theory]
    [InlineData(SourceType.Guid, "uniqueidentifier")]
    [InlineData(SourceType.Boolean, "bit")]
    [InlineData(SourceType.Int32, "int")]
    [InlineData(SourceType.Int64, "bigint")]
    [InlineData(SourceType.Double, "float(53)")]
    [InlineData(SourceType.Choice, "int")]
    [InlineData(SourceType.Lookup, "uniqueidentifier")]
    public void Map_MapsScalarTypes(SourceType sourceType, string expected)
    {
        var column = new ColumnDefinition { LogicalName = "value", SourceType = sourceType };

        Assert.Equal(expected, SqlServerTypeMapper.Map(column).Declaration);
    }

    [Theory]
    [InlineData(100, "nvarchar(100)")]
    [InlineData(4000, "nvarchar(4000)")]
    [InlineData(4001, "nvarchar(max)")]
    public void Map_MapsStringLengths(int length, string expected)
    {
        var column = new ColumnDefinition
        {
            LogicalName = "name",
            SourceType = SourceType.String,
            MaxLength = length
        };

        Assert.Equal(expected, SqlServerTypeMapper.Map(column).Declaration);
    }

    [Fact]
    public void Map_ValidatesDecimalPrecisionAndScale()
    {
        var column = new ColumnDefinition
        {
            LogicalName = "amount",
            SourceType = SourceType.Decimal,
            Precision = 4,
            Scale = 5
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerTypeMapper.Map(column));
    }
}
