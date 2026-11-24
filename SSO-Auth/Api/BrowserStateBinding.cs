using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// Binds an in-progress SSO transaction (an OpenID <c>state</c> or a SAML <c>RelayState</c>) to the
/// browser that started it.
/// </summary>
/// <remarks>
/// The transaction itself lives on the server, so without a binding a callback URL produced by one
/// person's login at the identity provider completes in whichever browser opens it. An attacker can
/// use that to sign a victim into the attacker's Jellyfin account (login CSRF). The start endpoint
/// therefore sets a cookie with a random value that only the server-side transaction also knows, and
/// the callback refuses the transaction unless the browser presents the same value.
/// <para>
/// Over HTTPS the cookie uses the <c>__Host-</c> prefix (Secure, no Domain, Path=/), so neither a
/// sibling subdomain nor a plain-HTTP response can plant or overwrite it. Plain-HTTP installations get
/// an unprefixed cookie without Secure, which browsers would otherwise drop.
/// </para>
/// </remarks>
internal static class BrowserStateBinding
{
    /// <summary>
    /// The cookie name prefix used over HTTPS.
    /// </summary>
    internal const string SecureCookiePrefix = "__Host-sso-state-";

    /// <summary>
    /// The cookie name prefix used over plain HTTP.
    /// </summary>
    internal const string InsecureCookiePrefix = "sso-state-";

    /// <summary>
    /// The message returned when a callback arrives without the matching cookie.
    /// </summary>
    internal const string MismatchMessage = "This sign-in was not started in this browser, or the browser did not keep its sign-in cookie. Start the sign-in again from Jellyfin.";

    /// <summary>
    /// Creates a new random binding value.
    /// </summary>
    /// <returns>A 256-bit random value as hexadecimal text.</returns>
    internal static string CreateValue() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Returns whether the browser reaches Jellyfin over HTTPS, judged by the public base URL.
    /// </summary>
    /// <param name="publicBaseUrl">The base URL the plugin uses for its own callbacks.</param>
    /// <returns><c>true</c> for an https base URL.</returns>
    internal static bool IsSecure(string publicBaseUrl)
        => publicBaseUrl != null && publicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the cookie name for one transaction. Each transaction gets its own cookie, so sign-ins
    /// started in several tabs do not overwrite each other.
    /// </summary>
    /// <param name="transactionKey">The OpenID state or SAML RelayState.</param>
    /// <param name="secure">Whether the browser uses HTTPS.</param>
    /// <returns>The cookie name.</returns>
    internal static string CookieName(string transactionKey, bool secure)
    {
        ArgumentException.ThrowIfNullOrEmpty(transactionKey);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(transactionKey));
        return (secure ? SecureCookiePrefix : InsecureCookiePrefix) + Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the cookie options.
    /// </summary>
    /// <param name="secure">Whether the browser uses HTTPS.</param>
    /// <param name="crossSitePost">
    /// Whether the callback arrives as a cross-site POST (SAML HTTP-POST binding). Lax cookies are not
    /// sent on those, so the cookie has to be SameSite=None, which browsers accept only with Secure.
    /// </param>
    /// <param name="lifetime">How long the cookie may live; <c>null</c> for a deletion.</param>
    /// <returns>The cookie options.</returns>
    internal static CookieOptions CreateOptions(bool secure, bool crossSitePost, TimeSpan? lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = crossSitePost && secure ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            MaxAge = lifetime,
            IsEssential = true,
        };
    }

    /// <summary>
    /// Sets the binding cookie for a transaction that is starting.
    /// </summary>
    /// <param name="response">The response of the start endpoint.</param>
    /// <param name="transactionKey">The OpenID state or SAML RelayState.</param>
    /// <param name="value">The binding value stored with the transaction.</param>
    /// <param name="secure">Whether the browser uses HTTPS.</param>
    /// <param name="crossSitePost">Whether the callback arrives as a cross-site POST.</param>
    /// <param name="lifetime">How long the transaction stays valid.</param>
    internal static void Issue(HttpResponse response, string transactionKey, string value, bool secure, bool crossSitePost, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(CookieName(transactionKey, secure), value, CreateOptions(secure, crossSitePost, lifetime));
    }

    /// <summary>
    /// Checks the binding cookie on a callback and always clears it.
    /// </summary>
    /// <param name="request">The callback request.</param>
    /// <param name="response">The callback response.</param>
    /// <param name="transactionKey">The OpenID state or SAML RelayState.</param>
    /// <param name="expected">The binding value stored with the transaction.</param>
    /// <param name="secure">Whether the browser uses HTTPS.</param>
    /// <param name="crossSitePost">Whether the callback arrives as a cross-site POST.</param>
    /// <returns><c>true</c> when the browser presented the expected value.</returns>
    internal static bool Verify(HttpRequest request, HttpResponse response, string transactionKey, string expected, bool secure, bool crossSitePost)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var name = CookieName(transactionKey, secure);
        request.Cookies.TryGetValue(name, out var presented);
        response.Cookies.Delete(name, CreateOptions(secure, crossSitePost, null));
        return Matches(expected, presented);
    }

    /// <summary>
    /// Compares two binding values in constant time.
    /// </summary>
    /// <param name="expected">The value stored with the transaction.</param>
    /// <param name="presented">The value the browser sent.</param>
    /// <returns><c>true</c> when both are present and equal.</returns>
    internal static bool Matches(string expected, string presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }
}
