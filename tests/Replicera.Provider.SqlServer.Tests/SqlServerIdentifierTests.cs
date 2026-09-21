namespace Replicera.Provider.SqlServer.Tests;

public sealed class SqlServerIdentifierTests
{
    [Theory]
    [InlineData("account", "account")]
    [InlineData("order detail", "order_detail")]
    [InlineData("1account", "_1account")]
    public void Normalize_ProducesPortableIdentifier(string source, string expected)
    {
        Assert.Equal(expected, SqlServerIdentifier.Normalize(source));
    }

    [Fact]
    public void Normalize_TruncatesDeterministicallyWithHash()
    {
        var source = new string('a', 140);

        var first = SqlServerIdentifier.Normalize(source);
        var second = SqlServerIdentifier.Normalize(source);

        Assert.Equal(SqlServerIdentifier.MaximumLength, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void NormalizeDistinct_RejectsCollision()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerIdentifier.NormalizeDistinct(["order detail", "order-detail"]));

        Assert.Contains("both normalize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_EscapesClosingBracket()
    {
        Assert.Equal("[odd]]name]", SqlServerIdentifier.Quote("odd]name"));
    }
}
