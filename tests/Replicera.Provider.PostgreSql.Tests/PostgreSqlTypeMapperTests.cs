using Replicera.Core.Models;

namespace Replicera.Provider.PostgreSql.Tests;

public sealed class PostgreSqlTypeMapperTests
{
    [Theory]
    [InlineData(SourceType.Guid, "uuid")]
    [InlineData(SourceType.Boolean, "boolean")]
    [InlineData(SourceType.Int32, "integer")]
    [InlineData(SourceType.Int64, "bigint")]
    [InlineData(SourceType.Double, "double precision")]
    [InlineData(SourceType.Choice, "integer")]
    [InlineData(SourceType.Lookup, "uuid")]
    public void Map_MapsScalarTypes(SourceType sourceType, string expected)
    {
        var column = new ColumnDefinition { LogicalName = "value", SourceType = sourceType };
        Assert.Equal(expected, PostgreSqlTypeMapper.Map(column).Declaration);
    }

    [Fact]
    public void Map_MapsBoundedAndUnboundedStrings()
    {
        Assert.Equal("character varying(100)", PostgreSqlTypeMapper.Map(
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String, MaxLength = 100 }).Declaration);
        Assert.Equal("text", PostgreSqlTypeMapper.Map(
            new ColumnDefinition { LogicalName = "name", SourceType = SourceType.String }).Declaration);
    }
}
