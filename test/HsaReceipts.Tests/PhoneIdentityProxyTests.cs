namespace HsaReceipts.Tests;

public sealed class PhoneIdentityProxyTests
{
    [Theory]
    [InlineData("/realms/household", true)]
    [InlineData("/realms/household/protocol/openid-connect/auth", true)]
    [InlineData("/resources/abc/login.css", true)]
    [InlineData("/realms/master", false)]
    [InlineData("/realms/household-admin", false)]
    [InlineData("/admin/master/console", false)]
    [InlineData("/realms%2Fhousehold/protocol/openid-connect/auth", false)]
    public void AllowsOnlyRequiredIdentityPaths(string path, bool expected)
    {
        Assert.Equal(expected, PhoneIdentityProxy.IsAllowedPath(path));
    }

    [Fact]
    public void RemovesApplicationCookiesButPreservesIdentityCookies()
    {
        const string cookies =
            "__Host-HsaReceipts=private; AUTH_SESSION_ID=identity; " +
            "__Host-HsaReceipts-Antiforgery=private-too; KC_RESTART=restart";

        var sanitized = PhoneIdentityProxy.RemovePrivateCookies(cookies);

        Assert.Equal("AUTH_SESSION_ID=identity; KC_RESTART=restart", sanitized);
    }

    [Fact]
    public void PublishedWorkerKeepsIdentityRoutesOnlineOnly()
    {
        var worker = File.ReadAllText(Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../src/HsaReceipts.Client/wwwroot/service-worker.published.js")));

        Assert.Contains("url.pathname.startsWith('/realms/')", worker);
        Assert.Contains("url.pathname.startsWith('/resources/')", worker);
        Assert.Contains("url.pathname.startsWith('/admin/')", worker);
    }
}
