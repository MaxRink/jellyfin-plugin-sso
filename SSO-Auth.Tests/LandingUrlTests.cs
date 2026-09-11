using Jellyfin.Plugin.SSO_Auth;

namespace SSO_Auth.Tests;

/// <summary>
/// Checks the page the login handoff sends the browser to.
/// </summary>
public class LandingUrlTests
{
    private const string BaseUrl = "https://jellyfin.example.com";

    private static string Generate(string? quickConnectCode = null, string? returnUrl = null)
        => WebResponse.Generator("state", "authelia", BaseUrl, "OID", quickConnectCode, returnUrl);

    [Fact]
    public void DefaultsToTheWebClientRoot()
    {
        Assert.Contains("window.location.replace(\"https://jellyfin.example.com/web/index.html\")", Generate(), StringComparison.Ordinal);
    }

    [Fact]
    public void OpensTheRequestedPage()
    {
        Assert.Contains("window.location.replace(\"https://jellyfin.example.com/web/index.html#/movies\")", Generate(returnUrl: "/movies"), StringComparison.Ordinal);
    }

    [Fact]
    public void PrefersQuickConnectOverTheRequestedPage()
    {
        var html = Generate(quickConnectCode: "123456", returnUrl: "/movies");

        Assert.Contains("#!/quickconnect?code=123456", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#/movies", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesTheRequestedPageSoItCannotBreakOutOfTheScript()
    {
        // A quote survives SanitizeReturnUrl, so the JSON encoding is what keeps it inside the
        // JavaScript string literal.
        var html = Generate(returnUrl: "/a\"+alert(1)+\"b");

        Assert.DoesNotContain("\"+alert(1)+\"", html, StringComparison.Ordinal);
        Assert.Contains("\\u0022", html, StringComparison.Ordinal);
    }
}
