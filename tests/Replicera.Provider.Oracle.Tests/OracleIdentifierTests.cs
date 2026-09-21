namespace Replicera.Provider.Oracle.Tests;

public sealed class OracleIdentifierTests
{
    [Theory]
    [InlineData("account", "ACCOUNT")]
    [InlineData("order detail", "ORDER_DETAIL")]
    [InlineData("123name", "_123NAME")]
    public void Normalize_ProducesPortableUppercaseNames(string source, string expected) =>
        Assert.Equal(expected, OracleIdentifier.Normalize(source));

    [Fact]
    public void Normalize_RespectsByteLimitWithStableHash()
    {
        var result = OracleIdentifier.Normalize(new string('é', 100));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= OracleIdentifier.MaximumByteLength);
        Assert.Equal(result, OracleIdentifier.Normalize(new string('é', 100)));
    }
}
