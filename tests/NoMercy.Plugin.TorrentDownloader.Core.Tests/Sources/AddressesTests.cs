using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

public class AddressesTests
{
    /// <remarks>
    /// <strong>G1.</strong> Every name in
    /// <c>api_?key|apikey|passkey|token|secret|rss_?key</c>, because an error
    /// message reaches a page, a log and the journal, and a secret in any of
    /// them is a secret published.
    /// </remarks>
    [Theory]
    [InlineData("https://x.example/?apikey=hunter2", "apikey")]
    [InlineData("https://x.example/?api_key=hunter2", "api_key")]
    [InlineData("https://x.example/?APIKEY=hunter2", "APIKEY")]
    [InlineData("https://x.example/?passkey=hunter2", "passkey")]
    [InlineData("https://x.example/?token=hunter2", "token")]
    [InlineData("https://x.example/?secret=hunter2", "secret")]
    [InlineData("https://x.example/?rss_key=hunter2", "rss_key")]
    [InlineData("https://x.example/?rsskey=hunter2", "rsskey")]
    public void ASecretParameterIsBlanked(string address, string name)
    {
        string redacted = Addresses.Redact(new(address));

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains($"{name}={Addresses.Blanked}", redacted, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The name stays and only the value goes. Which parameter it had is worth
    /// knowing when working out what went wrong; what was in it is exactly what
    /// must not be.
    /// </remarks>
    [Fact]
    public void EverythingThatIsNotASecretSurvives()
    {
        string redacted = Addresses.Redact(new("https://x.example/api?t=search&q=Silo+S03E06&apikey=hunter2&limit=100"));

        Assert.Contains("t=search", redacted, StringComparison.Ordinal);
        Assert.Contains("q=Silo+S03E06", redacted, StringComparison.Ordinal);
        Assert.Contains("limit=100", redacted, StringComparison.Ordinal);
        Assert.Contains("x.example/api", redacted, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A parameter that merely looks like a secret is not one. A passkey is
    /// forty hex characters and so is an info hash: blanking by shape would
    /// make every address useless for working out what went wrong.
    /// </remarks>
    [Fact]
    public void AValueThatMerelyLooksSecretIsLeftAlone()
    {
        string redacted = Addresses.Redact(
            new("https://x.example/?hash=0123456789abcdef0123456789abcdef01234567"));

        Assert.Contains("0123456789abcdef0123456789abcdef01234567", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressWithNoQueryIsUnchanged()
    {
        Assert.Equal("https://x.example/search/silo", Addresses.Redact(new("https://x.example/search/silo")));
    }

    /// <remarks>
    /// Not being able to read an address is not a reason to publish it.
    /// </remarks>
    [Fact]
    public void NoAddressSaysSoRatherThanNothing()
    {
        Assert.Equal("(no address)", Addresses.Redact(null));
    }
}
