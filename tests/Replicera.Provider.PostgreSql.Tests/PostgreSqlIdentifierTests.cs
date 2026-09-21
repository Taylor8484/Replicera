namespace Replicera.Provider.PostgreSql.Tests;

public sealed class PostgreSqlIdentifierTests
{
    [Theory]
    [InlineData("Account", "account")]
    [InlineData("order detail", "order_detail")]
    [InlineData("123name", "_123name")]
    public void Normalize_ProducesPortableLowercaseNames(string source, string expected) =>
        Assert.Equal(expected, PostgreSqlIdentifier.Normalize(source));

    [Fact]
    public void Normalize_TruncatesToPostgreSqlByteLimitWithStableHash()
    {
        var result = PostgreSqlIdentifier.Normalize(new string('é', 80));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= PostgreSqlIdentifier.MaximumByteLength);
        Assert.Equal(result, PostgreSqlIdentifier.Normalize(new string('é', 80)));
    }

    [Fact]
    public void NormalizeDistinct_RejectsCaseFoldedCollision() =>
        Assert.Throws<InvalidOperationException>(() => PostgreSqlIdentifier.NormalizeDistinct(["Name", "name"]));
}
