using Replicera.Core.Models;

namespace Replicera.Provider.Oracle.Tests;

public sealed class OracleTypeMapperTests
{
    [Theory]
    [InlineData(SourceType.Guid, "RAW(16)")]
    [InlineData(SourceType.Boolean, "NUMBER(1)")]
    [InlineData(SourceType.Int32, "NUMBER(10)")]
    [InlineData(SourceType.Int64, "NUMBER(19)")]
    [InlineData(SourceType.Double, "BINARY_DOUBLE")]
    [InlineData(SourceType.Lookup, "RAW(16)")]
    public void Map_MapsScalarTypes(SourceType sourceType, string expected)
    {
        var column = new ColumnDefinition { LogicalName = "value", SourceType = sourceType };
        Assert.Equal(expected, OracleTypeMapper.Map(column).Declaration);
    }

    [Fact]
    public void Map_UsesNClobForLongStrings()
    {
        Assert.Equal("NVARCHAR2(100)", OracleTypeMapper.Map(
            new ColumnDefinition { LogicalName = "short", SourceType = SourceType.String, MaxLength = 100 }).Declaration);
        Assert.Equal("NCLOB", OracleTypeMapper.Map(
            new ColumnDefinition { LogicalName = "long", SourceType = SourceType.String, MaxLength = 4000 }).Declaration);
    }
}
