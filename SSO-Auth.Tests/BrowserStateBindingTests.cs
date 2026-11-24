using System.Collections.Concurrent;
using System.Reflection;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace SSO_Auth.Tests;

/// <summary>
/// Tests for the cookie that binds an OpenID state or SAML RelayState to the browser that started it.
/// </summary>
public class BrowserStateBindingTests
{
    [Fact]
    public void SecureCookieUsesHostPrefixAndStrictAttributes()
    {
        var context = new DefaultHttpContext();
        BrowserStateBinding.Issue(context.Response, "state-1", "value", secure: true, crossSitePost: false, TimeSpan.FromMinutes(10));

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(BrowserStateBinding.SecureCookiePrefix, header, StringComparison.Ordinal);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SamlCookieIsSameSiteNoneSoTheCrossSitePostCarriesIt()
    {
        var context = new DefaultHttpContext();
        BrowserStateBinding.Issue(context.Response, "relay-1", "value", secure: true, crossSitePost: true, TimeSpan.FromMinutes(10));

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(BrowserStateBinding.SecureCookiePrefix, header, StringComparison.Ordinal);
        Assert.Contains("samesite=none", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainHttpCookieHasNoHostPrefixAndNoSecureFlag()
    {
        var context = new DefaultHttpContext();
        BrowserStateBinding.Issue(context.Response, "state-1", "value", secure: false, crossSitePost: false, TimeSpan.FromMinutes(10));

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(BrowserStateBinding.InsecureCookiePrefix, header, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CookieNamesDifferPerTransactionAndDoNotContainTheState()
    {
        var first = BrowserStateBinding.CookieName("state-1", true);
        var second = BrowserStateBinding.CookieName("state-2", true);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("state-1", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("wrong", false)]
    [InlineData("expected", true)]
    public void VerifyAcceptsOnlyTheMatchingCookieAndAlwaysClearsIt(string? presented, bool accepted)
    {
        var context = new DefaultHttpContext();
        var name = BrowserStateBinding.CookieName("state-1", true);
        if (presented is not null)
        {
            context.Request.Headers.Cookie = $"{name}={presented}";
        }

        Assert.Equal(accepted, BrowserStateBinding.Verify(context.Request, context.Response, "state-1", "expected", secure: true, crossSitePost: false));

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(name + "=", header, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", header, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("expected", null)]
    [InlineData(null, "expected")]
    [InlineData("expected", "expecteD")]
    [InlineData("expected", "expected2")]
    public void MatchesRejectsMissingOrDifferentValues(string? expected, string? presented)
    {
        Assert.False(BrowserStateBinding.Matches(expected!, presented!));
    }

    [Theory]
    [InlineData("https://media.example.com", true)]
    [InlineData("HTTPS://media.example.com/jellyfin", true)]
    [InlineData("http://192.168.1.10:8096", false)]
    [InlineData(null, false)]
    public void SecurityFollowsThePublicBaseUrl(string? baseUrl, bool secure)
    {
        Assert.Equal(secure, BrowserStateBinding.IsSecure(baseUrl!));
    }
}

/// <summary>
/// Callback-level tests for the browser binding, sharing the controller fixture.
/// </summary>
public partial class OidDeviceAuthTests
{
    private const string PublicHost = "media.example.com";

    private static ConcurrentDictionary<string, TimedAuthorizeState> OidStateStore()
        => (ConcurrentDictionary<string, TimedAuthorizeState>)typeof(SSOController)
            .GetField("StateManager", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static ConcurrentDictionary<string, TimedSamlLinkState> SamlStateStore()
        => (ConcurrentDictionary<string, TimedSamlLinkState>)typeof(SSOController)
            .GetField("SamlLinkStateManager", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private void UseHttpsRequest(string? cookie)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(PublicHost);
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }

        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenIdCallbackWithoutTheBrowserCookieIsRefusedAndConsumesTheState(bool wrongValue)
    {
        SSOPlugin.Instance.Configuration.OidConfigs[Provider] = new OidConfig
        {
            Enabled = true,
            OidEndpoint = Issuer,
            OidClientId = ClientId,
        };
        var state = Guid.NewGuid().ToString("N");
        OidStateStore()[state] = new TimedAuthorizeState(new AuthorizeState { State = state }, DateTime.UtcNow)
        {
            Provider = Provider,
            BrowserBinding = "expected-binding",
        };
        UseHttpsRequest(wrongValue ? $"{BrowserStateBinding.CookieName(state, true)}=attacker-binding" : null);

        try
        {
            var result = Assert.IsType<ContentResult>(await _controller.OidPost(Provider, state));
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Equal(BrowserStateBinding.MismatchMessage, result.Content);
            Assert.False(OidStateStore().ContainsKey(state));
            _sessionManager.Verify(m => m.AuthenticateDirect(It.IsAny<MediaBrowser.Controller.Session.AuthenticationRequest>()), Times.Never);
        }
        finally
        {
            OidStateStore().TryRemove(state, out _);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("attacker-binding", true)]
    [InlineData("expected-binding", false)]
    public async Task SamlResponseNeedsTheBrowserCookieOfItsRelayState(string? presented, bool refused)
    {
        SSOPlugin.Instance.Configuration.SamlConfigs[Provider] = new SamlConfig { Enabled = true, SamlClientId = "jellyfin" };
        var relayState = "link:" + Guid.NewGuid().ToString("N");
        SamlStateStore()[relayState] = new TimedSamlLinkState(Guid.Empty, Provider, "request-id", "https://" + PublicHost + "/sso/SAML/post/" + Provider, DateTime.UtcNow)
        {
            BrowserBinding = "expected-binding",
        };
        UseHttpsRequest(presented is null ? null : $"{BrowserStateBinding.CookieName(relayState, true)}={presented}");

        try
        {
            var result = await _controller.SamlPost(Provider, relayState);
            if (refused)
            {
                var content = Assert.IsType<ContentResult>(result);
                Assert.Equal(StatusCodes.Status400BadRequest, content.StatusCode);
                Assert.Equal(BrowserStateBinding.MismatchMessage, content.Content);
            }
            else
            {
                // Past the binding, the empty request fails on the missing SAML response instead.
                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal("Malformed SAML response.", badRequest.Value);
            }

            Assert.False(SamlStateStore().ContainsKey(relayState));
        }
        finally
        {
            SamlStateStore().TryRemove(relayState, out _);
        }
    }
}
