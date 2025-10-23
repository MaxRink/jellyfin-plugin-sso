using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSO_Auth.Lib;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// The sso api controller.
/// </summary>
[ApiController]
[Route("[controller]")]
public class SSOController : ControllerBase
{
    private const string SamlLinkStatePrefix = "link:";
    private const string UsernameTakenMessage = "A Jellyfin account with this username already exists and is not linked to this provider. Link it from the account linking page, or allow username account adoption for this provider.";
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<SSOController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly TimeSpan AuthorizationStateLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, TimedAuthorizeState> StateManager = new();
    private static readonly ConcurrentDictionary<string, TimedSamlLinkState> SamlLinkStateManager = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOController}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="cryptoProvider">Instance of the <see cref="ICryptoProvider"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public SSOController(
        ILogger<SSOController> logger,
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        IUserManager userManager,
        IAuthorizationContext authContext,
        ICryptoProvider cryptoProvider,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager serverConfigurationManager)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _authContext = authContext;
        _cryptoProvider = cryptoProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
        _logger.LogInformation("SSO Controller initialized");
    }

    /// <summary>
    /// The GET endpoint for OpenID provider to callback to. Returns a webpage that parses client data and completes auth.
    /// </summary>
    /// <param name="provider">The ID of the provider which will use the callback information.</param>
    /// <param name="state">The current request state.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    // Actually a GET: https://github.com/IdentityModel/IdentityModel.OidcClient/issues/325
    [HttpGet("OID/r/{provider}")]
    [HttpGet("OID/redirect/{provider}")]
    public async Task<ActionResult> OidPost(
        [FromRoute] string provider,
        [FromQuery] string state) // Although this is a GET function, this function is called `Post` for consistency with SAML
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            if (string.IsNullOrEmpty(state))
            {
                return BadRequest("Missing state");
            }

            if (!StateManager.TryGetValue(state, out var timedState))
            {
                return BadRequest("Invalid or expired state");
            }

            if (IsAuthorizationStateExpired(timedState.Created))
            {
                StateManager.TryRemove(state, out _);
                return BadRequest("Invalid or expired state");
            }

            if (!string.Equals(timedState.Provider, provider, StringComparison.Ordinal))
            {
                return BadRequest("The authorization state belongs to a different provider.");
            }

            if (timedState.IsLinking && !StateManager.TryRemove(state, out timedState))
            {
                return BadRequest("Invalid or expired state");
            }

            var scopes = config.OidScopes ?? Array.Empty<string>();
            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint?.Trim(),
                ClientId = config.OidClientId?.Trim(),
                ClientSecret = config.OidSecret?.Trim(),
                RedirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/{(Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase) ? "redirect" : "r")}/" + provider,
                Scope = string.Join(" ", scopes.Prepend("openid profile")),
                DisablePushedAuthorization = config.DisablePushedAuthorization,
                LoggerFactory = _loggerFactory,
                LoadProfile = !config.DoNotLoadProfile,
                HttpClientFactory = o =>
                {
                    var client = _httpClientFactory.CreateClient();
                    System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    string version = fvi.FileVersion;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SSO-Auth +{version} (https://github.com/9p4/jellyfin-plugin-sso)");
                    return client;
                }
            };
            var oidEndpointUri = new Uri(config.OidEndpoint?.Trim());
            options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(oidEndpointUri.GetLeftPart(UriPartial.Authority));
            options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints; // For Google and other providers with different endpoints
            options.Policy.Discovery.RequireHttps = !config.DisableHttps;
            options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;

            WarnAboutRelaxedDiscovery(config);
            var oidcClient = new OidcClient(options);
            var currentState = timedState.State;
            LoginResult result;
            try
            {
                result = await oidcClient.ProcessResponseAsync(Request.QueryString.Value, currentState).ConfigureAwait(false);
            }
            catch (ArgumentNullException ex) when (!config.DoNotLoadProfile && string.Equals(ex.ParamName, "source", StringComparison.Ordinal))
            {
                _logger.LogError(ex, "Failed to parse the OIDC UserInfo response for provider {Provider}", provider);
                return ReturnError(
                    StatusCodes.Status400BadRequest,
                    "The OIDC UserInfo response could not be parsed. Cloudflare Access users must enable 'Skip OIDC UserInfo Request' in this provider's settings.");
            }

            if (result.IsError)
            {
                StateManager.TryRemove(state, out _);
                return ReturnError(StatusCodes.Status400BadRequest, $"Error logging in: {result.Error} - {result.ErrorDescription}");
            }

            if (config.EnabledFolders != null)
            {
                timedState.Folders = new HashSet<string>(config.EnabledFolders);
            }
            else
            {
                timedState.Folders = new HashSet<string>();
            }

            timedState.EnableLiveTv = config.EnableLiveTv;
            timedState.EnableLiveTvManagement = config.EnableLiveTvManagement;

            if (config.AvatarUrlFormat is not null)
            {
                timedState.AvatarURL = result.User.Claims.Aggregate(
                    config.AvatarUrlFormat,
                    (s, claim) => s.Contains($"@{{{claim.Type}}}") ? s.Replace($"@{{{claim.Type}}}", claim.Value) : s);
            }

            var roleClaimPaths = ParseRoleClaimPaths(config.RoleClaim).ToArray();

            foreach (var claim in result.User.Claims)
            {
                if (claim.Type == (config.DefaultUsernameClaim?.Trim() ?? "preferred_username"))
                {
                    timedState.Username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        timedState.Valid = true;
                    }
                }

                if (claim.Type == "sub")
                {
                    timedState.Id = claim.Value;
                }

                // Role processing
                foreach (var roleClaimPath in roleClaimPaths)
                {
                    if (claim.Type == roleClaimPath[0])
                    {
                        List<string> roles = GetRolesFromClaimPath(claim, roleClaimPath);

                        foreach (string role in roles)
                        {
                            // Check if allowed to login based on roles
                            if (config.Roles != null && config.Roles.Any())
                            {
                                foreach (string validRoles in config.Roles)
                                {
                                    if (role.Equals(validRoles))
                                    {
                                        timedState.Valid = true;
                                    }
                                }
                            }

                            // Check if admin based on roles
                            if (config.AdminRoles != null && config.AdminRoles.Any())
                            {
                                foreach (string validAdminRoles in config.AdminRoles)
                                {
                                    if (role.Equals(validAdminRoles))
                                    {
                                        timedState.Admin = true;
                                    }
                                }
                            }

                            // Get allowed folders from roles
                            if (config.EnableFolderRoles)
                            {
                                foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                                {
                                    if (role.Equals(folderRoleMap.Role?.Trim()))
                                    {
                                        timedState.Folders.UnionWith(folderRoleMap.Folders);
                                    }
                                }
                            }

                            if (config.EnableLiveTvRoles)
                            {
                                // Check if allowed Live TV based on roles
                                if (config.LiveTvRoles != null && config.LiveTvRoles.Any())
                                {
                                    foreach (string validLiveTvRoles in config.LiveTvRoles)
                                    {
                                        if (role.Equals(validLiveTvRoles))
                                        {
                                            timedState.EnableLiveTv = true;
                                        }
                                    }
                                }

                                // Check if allowed Live TV management based on roles
                                if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Any())
                                {
                                    foreach (string validLiveTvManagementRoles in config.LiveTvManagementRoles)
                                    {
                                        if (role.Equals(validLiveTvManagementRoles))
                                        {
                                            timedState.EnableLiveTvManagement = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // If the provider doesn't support the preferred username claim, then use the sub claim
            if (!timedState.Valid)
            {
                timedState.Username = timedState.Id;
                if (config.Roles == null || config.Roles.Length == 0)
                {
                    timedState.Valid = true;
                }
            }

            bool isLinking = timedState.IsLinking;

            if (timedState.Valid && string.IsNullOrWhiteSpace(timedState.Username))
            {
                StateManager.TryRemove(state, out _);
                return BadRequest("The OpenID provider did not return a usable user identifier.");
            }

            if (config.AdminRoles != null && config.AdminRoles.Length > 0)
            {
                if (timedState.Admin)
                {
                    _logger.LogInformation(
                        "OpenID user {Username} matched configured admin roles {@AdminRoles}.",
                        timedState.Username,
                        config.AdminRoles);
                }
                else
                {
                    _logger.LogWarning(
                        "OpenID user {Username} did not match any configured admin role {@AdminRoles}. RoleClaim={RoleClaim}. Claims seen: {@Claims}. Note: matching is case-sensitive and exact-string.",
                        timedState.Username,
                        config.AdminRoles,
                        config.RoleClaim,
                        result.User.Claims.Select(o => new { o.Type, o.Value }));
                }
            }

            if (timedState.Valid)
            {
                if (isLinking)
                {
                    if (!timedState.LinkingUserId.HasValue)
                    {
                        StateManager.TryRemove(state, out _);
                        return BadRequest("The linking transaction is not associated with a Jellyfin user.");
                    }

                    var linkResult = CreateCanonicalLink("oid", provider, timedState.LinkingUserId.Value, timedState.Id);
                    if (linkResult is not NoContentResult)
                    {
                        StateManager.TryRemove(state, out _);
                        return linkResult;
                    }

                    StateManager.TryRemove(state, out _);
                    return Redirect(GetRequestBase(config.SchemeOverride, config.PortOverride) + "/SSOViews/linking");
                }

                _logger.LogInformation($"Is request linking: {isLinking}");
                return Content(WebResponse.Generator(data: state, provider: provider, baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride), mode: "OID", quickConnectCode: timedState.QuickConnectCode), MediaTypeNames.Text.Html);
            }
            else
            {
                StateManager.TryRemove(state, out _);

                // Only the claim names at warning level: the values carry the user's email,
                // real name and group memberships, which do not belong in the server log
                // just because someone failed a permission check. The full set is available
                // at debug level for working out why a provider is not matching.
                _logger.LogWarning(
                    "OpenID user {Username} has no matching role claim. Expected any one of: {@ExpectedRoles}. Claims returned: {@ClaimTypes}",
                    timedState.Username,
                    config.Roles,
                    result.User.Claims.Select(claim => claim.Type).Distinct());

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Claims returned for {Username}: {@Claims}",
                        timedState.Username,
                        result.User.Claims.Select(claim => new { claim.Type, claim.Value }));
                }

                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }
        }

        // If the config doesn't have an active provider matching the requeset, show an error
        return BadRequest("No matching provider found");
    }

    private static IEnumerable<string[]> ParseRoleClaimPaths(string roleClaim)
    {
        if (string.IsNullOrWhiteSpace(roleClaim))
        {
            return Enumerable.Empty<string[]>();
        }

        // Role claim paths are space-separated. Escape a literal space as "\ ".
        // Dots inside one path still mean JSON traversal. Escape a literal dot as "\.".
        return Regex.Split(roleClaim.Trim(), "(?<!\\\\) +")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Regex.Split(path.Replace("\\ ", " "), "(?<!\\\\)\\.")
                .Select(segment => segment.Replace("\\.", "."))
                .ToArray())
            .Where(segments => segments.Length > 0 && !string.IsNullOrWhiteSpace(segments[0]));
    }

    private static List<string> GetRolesFromClaimPath(Claim claim, string[] segments)
    {
        if (segments.Length == 1)
        {
            // Providers such as Zitadel encode roles as the keys of a JSON object,
            // e.g. {"jellyfin_admin": {"org_id": "org_domain"}, "jellyfin_user": {...}}.
            if (claim.Value.TrimStart().StartsWith('{'))
            {
                try
                {
                    var keys = JsonConvert.DeserializeObject<IDictionary<string, object>>(claim.Value)?.Keys.ToList();
                    if (keys is not null)
                    {
                        return keys;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON after all: treat the raw value as a single role below.
                }
            }

            return new List<string> { claim.Value };
        }

        // Traverse a JSON object claim, such as "realm_access.roles".
        var json = JsonConvert.DeserializeObject<IDictionary<string, object>>(claim.Value);
        if (json is null)
        {
            return new List<string>();
        }

        for (int i = 1; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (!json.TryGetValue(segment, out var nextToken) || nextToken is not JObject nextObject)
            {
                return new List<string>();
            }

            json = nextObject.ToObject<IDictionary<string, object>>();
            if (json is null)
            {
                return new List<string>();
            }
        }

        if (!json.TryGetValue(segments[^1], out var rolesToken))
        {
            return new List<string>();
        }

        return rolesToken switch
        {
            JArray rolesArray => rolesArray.ToObject<List<string>>() ?? new List<string>(),
            JObject rolesObject => rolesObject.Properties().Select(p => p.Name).ToList(),
            _ => new List<string>(),
        };
    }

    /// <summary>
    /// Initiates the login flow for OpenID. This redirects the user to the auth provider.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="isLinking">Whether or not this request is to link accounts (Rather than authenticate).</param>
    /// <param name="qc">Optional Jellyfin Quick Connect code to prefill after authentication.</param>
    /// <returns>An asynchronous result for the authentication.</returns>
    [HttpGet("OID/p/{provider}")]
    [HttpGet("OID/start/{provider}")]
    public async Task<ActionResult> OidChallenge(string provider, [FromQuery] bool isLinking = false, [FromQuery] string qc = null)
    {
        if (isLinking)
        {
            return BadRequest("Linking must be started from the authenticated SSO linking page.");
        }

        return await StartOidChallenge(provider, false, null, false, qc).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an OpenID account-linking flow for the authenticated Jellyfin user.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <returns>The identity provider URL to navigate to.</returns>
    [Authorize]
    [HttpPost("OID/StartLink/{provider}")]
    [Produces(MediaTypeNames.Text.Plain)]
    public async Task<ActionResult> OidLinkChallenge(string provider)
    {
        var authorization = await _authContext.GetAuthorizationInfo(HttpContext.Request).ConfigureAwait(false);
        if (!authorization.IsAuthenticated || authorization.User is null)
        {
            return Unauthorized();
        }

        Guid jellyfinUserId = authorization.UserId;
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to link SSO providers.");
        }

        return await StartOidChallenge(provider, true, jellyfinUserId, true).ConfigureAwait(false);
    }

    private async Task<ActionResult> StartOidChallenge(string provider, bool isLinking, Guid? linkingUserId, bool returnStartUrl, string quickConnectCode = null)
    {
        Invalidate();
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            // Which callback alias this provider answers on is inferred from the alias the
            // login was started through, and remembered for the linking flow, which always
            // starts at the short "/p/" path and so cannot infer it for itself.
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);

                // Written through rather than only assigned: this used to mutate the
                // in-memory config object alone, so the choice was lost on restart and
                // linking fell back to "/r/" until someone logged in again. Guarded on a
                // change because this path runs on every single login.
                if (config.NewPath != newPath)
                {
                    config.NewPath = newPath;
                    SSOPlugin.Instance.UpdateConfiguration(SSOPlugin.Instance.Configuration);
                }
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/{(newPath ? "redirect" : "r")}/" + provider;

            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint?.Trim(),
                ClientId = config.OidClientId?.Trim(),
                ClientSecret = config.OidSecret?.Trim(),
                RedirectUri = redirectUri,
                Scope = string.Join(" ", (config.OidScopes ?? Array.Empty<string>()).Prepend("openid profile")),
                DisablePushedAuthorization = config.DisablePushedAuthorization,
                LoggerFactory = _loggerFactory,
                LoadProfile = !config.DoNotLoadProfile,
                HttpClientFactory = o =>
                {
                    var client = _httpClientFactory.CreateClient();
                    System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    string version = fvi.FileVersion;

                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SSO-Auth +{version} (https://github.com/9p4/jellyfin-plugin-sso)");
                    return client;
                }
            };
            var oidEndpointUri = new Uri(config.OidEndpoint?.Trim());
            options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(oidEndpointUri.GetLeftPart(UriPartial.Authority));
            options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints; // For Google and other providers with different endpoints
            options.Policy.Discovery.RequireHttps = !config.DisableHttps;
            options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;

            WarnAboutRelaxedDiscovery(config);
            var oidcClient = new OidcClient(options);
            var state = await oidcClient.PrepareLoginAsync().ConfigureAwait(false);

            if (state.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error preparing login: {state.Error} - {state.ErrorDescription}");
            }

            var timedState = new TimedAuthorizeState(state, DateTime.UtcNow)
            {
                IsLinking = isLinking,
                LinkingUserId = linkingUserId,
                Provider = provider,
                QuickConnectCode = quickConnectCode
            };

            if (!StateManager.TryAdd(state.State, timedState))
            {
                return StatusCode(StatusCodes.Status409Conflict, "An authorization flow with the same state already exists.");
            }

            if (returnStartUrl)
            {
                return Content(state.StartUrl, MediaTypeNames.Text.Plain);
            }

            return Redirect(state.StartUrl);
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds an OpenID auth configuration. Requires administrator privileges. If the provider already exists, it will be removed and readded.
    /// </summary>
    /// <param name="provider">The name of the provider to add.</param>
    /// <param name="config">The OID configuration (deserialized from a JSON post).</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("OID/Add/{provider}")]
    public void OidAdd(string provider, [FromBody] OidConfig config)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs[provider] = config;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Deletes an OpenID provider.
    /// </summary>
    /// <param name="provider">Name of provider to delete.</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Del/{provider}")]
    public void OidDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Lists the OpenID providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Get")]
    public ActionResult OidProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs);
    }

    /// <summary>
    /// Lists the OpenID providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("OID/GetNames")]
    public ActionResult OidProviderNames()
    {
        // Anonymous by necessity: a sign-in page has to render its provider buttons before
        // anyone is authenticated. Disabled providers are withheld, since nothing
        // unauthenticated can act on them.
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs
            .Where(provider => provider.Value.Enabled)
            .Select(provider => provider.Key)
            .ToList());
    }

    /// <summary>
    /// Lists the SAML providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("SAML/GetNames")]
    public ActionResult SamlProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs.Keys);
    }

    /// <summary>
    /// This is a debug endpoint to list all running OpenID flows. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID flows in progress.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/States")]
    public ActionResult OidStates()
    {
        return Ok(StateManager);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">Name of provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("OID/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidAuth(string provider, [FromBody] AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled
            && !string.IsNullOrEmpty(response.Data)
            && StateManager.TryGetValue(response.Data, out var pendingState)
            && pendingState.Valid
            && !IsAuthorizationStateExpired(pendingState.Created)
            && string.Equals(pendingState.Provider, provider, StringComparison.Ordinal)
            && StateManager.TryRemove(response.Data, out var timedState))
        {
            Guid? userId = await CreateCanonicalLinkAndUserIfNotExist("oid", provider, timedState.Id, timedState.Username);
            if (userId is null)
            {
                return Conflict(UsernameTakenMessage);
            }

            var authenticationResult = await Authenticate(
                userId.Value,
                timedState.Admin,
                config.EnableAuthorization,
                config.EnableAllFolders,
                timedState.Folders.ToArray(),
                timedState.EnableLiveTv,
                timedState.EnableLiveTvManagement,
                response,
                config.DefaultProvider?.Trim(),
                timedState.AvatarURL,
                config.PreserveAdminPermissions)
                .ConfigureAwait(false);
            return Ok(authenticationResult);
        }

        return BadRequest("Invalid or expired authorization state.");
    }

    /// <summary>
    /// Authenticates a user via an id_token obtained from the OAuth2 device code flow.
    /// Validates the JWT against the provider's JWKS and applies the same role/folder logic as the redirect flow.
    /// </summary>
    /// <param name="provider">Name of the OID provider to authenticate against.</param>
    /// <param name="request">The device auth request containing the id_token and client device info.</param>
    /// <returns>A Jellyfin authentication result on success.</returns>
    [HttpPost("OID/DeviceAuth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidDeviceAuth(string provider, [FromBody] DeviceAuthRequest request)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (!config.Enabled)
        {
            return BadRequest("Provider is not enabled");
        }

        if (string.IsNullOrEmpty(request?.IdToken))
        {
            return BadRequest("Missing id_token");
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();

            // Fetch OIDC discovery document to get issuer and JWKS URI
            var discoveryUrl = config.OidEndpoint?.Trim().TrimEnd('/') + "/.well-known/openid-configuration";
            var discoveryJson = await httpClient.GetStringAsync(discoveryUrl).ConfigureAwait(false);
            var discovery = JsonConvert.DeserializeObject<JObject>(discoveryJson);

            var jwksUri = discovery["jwks_uri"]?.ToString();
            var issuer = discovery["issuer"]?.ToString();

            if (string.IsNullOrEmpty(jwksUri))
            {
                return Problem("Could not determine JWKS URI from provider discovery document");
            }

            // Fetch JWKS and build signing keys
            var jwksJson = await httpClient.GetStringAsync(jwksUri).ConfigureAwait(false);
            var jwks = new Microsoft.IdentityModel.Tokens.JsonWebKeySet(jwksJson);

            var validationParams = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidAudience = config.OidClientId?.Trim(),
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidateIssuer = !config.DoNotValidateIssuerName,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };

            var handler = new JsonWebTokenHandler();
            var tokenResult = await handler.ValidateTokenAsync(request.IdToken, validationParams).ConfigureAwait(false);

            if (!tokenResult.IsValid)
            {
                _logger.LogWarning("Device auth JWT validation failed for provider {Provider}: {Exception}", provider, tokenResult.Exception?.Message);
                return StatusCode(StatusCodes.Status401Unauthorized, "Invalid or expired token");
            }

            var claims = tokenResult.ClaimsIdentity.Claims.ToList();

            // Apply the same username and role extraction logic as the redirect flow
            string username = null;
            string subject = null;
            bool valid = false;
            bool isAdmin = false;
            var folders = config.EnabledFolders != null ? new HashSet<string>(config.EnabledFolders) : new HashSet<string>();
            bool enableLiveTv = config.EnableLiveTv;
            bool enableLiveTvManagement = config.EnableLiveTvManagement;
            string avatarUrl = null;

            if (config.AvatarUrlFormat is not null)
            {
                avatarUrl = claims.Aggregate(
                    config.AvatarUrlFormat,
                    (s, claim) => s.Contains($"@{{{claim.Type}}}") ? s.Replace($"@{{{claim.Type}}}", claim.Value) : s);
            }

            var roleClaimPaths = ParseRoleClaimPaths(config.RoleClaim).ToArray();

            foreach (var claim in claims)
            {
                if (claim.Type == (config.DefaultUsernameClaim?.Trim() ?? "preferred_username"))
                {
                    username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        valid = true;
                    }
                }

                if (claim.Type == "sub")
                {
                    subject = claim.Value;
                }

                foreach (var roleClaimPath in roleClaimPaths)
                {
                    if (claim.Type != roleClaimPath[0])
                    {
                        continue;
                    }

                    List<string> roles = GetRolesFromClaimPath(claim, roleClaimPath);

                    foreach (string role in roles)
                    {
                        if (config.Roles != null && config.Roles.Any())
                        {
                            foreach (string validRole in config.Roles)
                            {
                                if (role.Equals(validRole))
                                {
                                    valid = true;
                                }
                            }
                        }

                        if (config.AdminRoles != null && config.AdminRoles.Any())
                        {
                            foreach (string adminRole in config.AdminRoles)
                            {
                                if (role.Equals(adminRole))
                                {
                                    isAdmin = true;
                                }
                            }
                        }

                        if (config.EnableFolderRoles)
                        {
                            foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                            {
                                if (role.Equals(folderRoleMap.Role?.Trim()))
                                {
                                    folders.UnionWith(folderRoleMap.Folders);
                                }
                            }
                        }

                        if (config.EnableLiveTvRoles)
                        {
                            if (config.LiveTvRoles != null && config.LiveTvRoles.Any())
                            {
                                foreach (string liveTvRole in config.LiveTvRoles)
                                {
                                    if (role.Equals(liveTvRole))
                                    {
                                        enableLiveTv = true;
                                    }
                                }
                            }

                            if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Any())
                            {
                                foreach (string liveTvMgmtRole in config.LiveTvManagementRoles)
                                {
                                    if (role.Equals(liveTvMgmtRole))
                                    {
                                        enableLiveTvManagement = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Fallback to "sub" claim if no preferred_username claim found or roles insufficient
            if (!valid)
            {
                username = subject;
                if (config.Roles == null || config.Roles.Length == 0)
                {
                    valid = true;
                }
            }

            if (!valid)
            {
                _logger.LogWarning(
                    "Device auth user {Username} has insufficient roles. Claims: {@Claims}. Expected any of: {@ExpectedRoles}",
                    username,
                    claims.Select(c => new { c.Type, c.Value }),
                    config.Roles);
                return StatusCode(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }

            var authResponse = new AuthResponse
            {
                DeviceID = request.DeviceID,
                DeviceName = request.DeviceName,
                AppName = request.AppName,
                AppVersion = request.AppVersion,
            };

            var userId = await CreateCanonicalLinkAndUserIfNotExist("oid", provider, subject, username).ConfigureAwait(false);
            if (userId is null)
            {
                return Conflict(UsernameTakenMessage);
            }

            var authenticationResult = await Authenticate(
                userId.Value,
                isAdmin,
                config.EnableAuthorization,
                config.EnableAllFolders,
                folders.ToArray(),
                enableLiveTv,
                enableLiveTvManagement,
                authResponse,
                config.DefaultProvider?.Trim(),
                avatarUrl,
                config.PreserveAdminPermissions)
                .ConfigureAwait(false);

            return Ok(authenticationResult);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during device auth for provider {Provider}", provider);
            return Problem("Authentication failed");
        }
    }

    /// <summary>
    /// This is the callback for the SAML flow. This creates a webpage to complete auth.
    /// </summary>
    /// <param name="provider">The provider that is calling back.</param>
    /// <param name="relayState">
    ///    RelayState given in the original SAML request. Authenticated linking flows use
    ///    a random, single-use value that identifies their server-side transaction.
    /// </param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    [HttpPost("SAML/p/{provider}")]
    [HttpPost("SAML/post/{provider}")]
    public ActionResult SamlPost(string provider, [FromQuery] string relayState = null)
    {
        if (string.IsNullOrEmpty(relayState) && Request.HasFormContentType)
        {
            relayState = Request.Form["RelayState"].FirstOrDefault();
        }

        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (string.Equals(relayState, "linking", StringComparison.Ordinal))
        {
            return BadRequest("Legacy unauthenticated linking transactions are no longer accepted.");
        }

        TimedSamlLinkState samlLinkState = null;
        bool isLinking = relayState?.StartsWith(SamlLinkStatePrefix, StringComparison.Ordinal) == true;
        if (isLinking)
        {
            if (!SamlLinkStateManager.TryRemove(relayState, out samlLinkState)
                || IsAuthorizationStateExpired(samlLinkState.Created)
                || !string.Equals(samlLinkState.Provider, provider, StringComparison.Ordinal))
            {
                return BadRequest("Invalid or expired SAML linking state.");
            }
        }

        _logger.LogInformation("SAML response received. Is linking: {IsLinking}", isLinking);

        if (config.Enabled)
        {
            var samlResponse = new Response(config.SamlCertificate, Request.Form["SAMLResponse"]);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            string providerUserId = samlResponse.GetNameID();
            if (string.IsNullOrWhiteSpace(providerUserId))
            {
                return BadRequest("The SAML provider did not return a usable NameID.");
            }

            if (isLinking && !samlResponse.IsResponseTo(samlLinkState.RequestId, samlLinkState.Recipient))
            {
                return BadRequest("The SAML response does not match the linking request.");
            }

            bool valid = false;

            // If no roles are configured, don't use RBAC
            if (config.Roles.Length == 0)
            {
                valid = true;
            }

            // Check if user is allowed to log in based on roles
            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                foreach (string allowedRole in config.Roles)
                {
                    if (allowedRole.Equals(role))
                    {
                        valid = true;
                    }
                }
            }

            if (valid)
            {
                if (isLinking)
                {
                    var linkResult = CreateCanonicalLink("saml", provider, samlLinkState.JellyfinUserId, providerUserId);
                    if (linkResult is not NoContentResult)
                    {
                        return linkResult;
                    }

                    return Redirect(GetRequestBase(config.SchemeOverride, config.PortOverride) + "/SSOViews/linking");
                }

                return Content(
                        WebResponse.Generator(
                            data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(samlResponse.Xml)),
                            provider: provider,
                            baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride),
                            mode: "SAML"),
                        MediaTypeNames.Text.Html);
            }

            _logger.LogWarning(
                "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                providerUserId,
                samlResponse.GetCustomAttributes("Role"),
                config.Roles);
            return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
        }

        return ReturnError(StatusCodes.Status400BadRequest, "No active providers found");
    }

    /// <summary>
    /// Initializes the SAML flow. This will redirect the user to the SAML provider.
    /// </summary>
    /// <param name="provider">The provider to being the flow with.</param>
    /// <param name="isLinking">Whether this flow intends to link an account, or initiate auth.</param>
    /// <returns>A redirect to the SAML provider's auth page.</returns>
    [HttpGet("SAML/p/{provider}")]
    [HttpGet("SAML/start/{provider}")]
    public ActionResult SamlChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        if (isLinking)
        {
            return BadRequest("Linking must be started from the authenticated SSO linking page.");
        }

        return StartSamlChallenge(provider, false, null, false);
    }

    /// <summary>
    /// Starts a SAML account-linking flow for the authenticated Jellyfin user.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <returns>The identity provider URL to navigate to.</returns>
    [Authorize]
    [HttpPost("SAML/StartLink/{provider}")]
    [Produces(MediaTypeNames.Text.Plain)]
    public async Task<ActionResult> SamlLinkChallenge(string provider)
    {
        var authorization = await _authContext.GetAuthorizationInfo(HttpContext.Request).ConfigureAwait(false);
        if (!authorization.IsAuthenticated || authorization.User is null)
        {
            return Unauthorized();
        }

        Guid jellyfinUserId = authorization.UserId;
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to link SSO providers.");
        }

        return StartSamlChallenge(provider, true, jellyfinUserId, true);
    }

    private ActionResult StartSamlChallenge(string provider, bool isLinking, Guid? linkingUserId, bool returnStartUrl)
    {
        Invalidate();
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);
                config.NewPath = newPath;
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/SAML/{(newPath ? "post" : "p")}/" + provider;

            var request = new AuthRequest(
                config.SamlClientId.Trim(),
                redirectUri);

            string relayState = null;
            if (isLinking)
            {
                if (!linkingUserId.HasValue)
                {
                    return BadRequest("The linking transaction is not associated with a Jellyfin user.");
                }

                relayState = SamlLinkStatePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var samlLinkState = new TimedSamlLinkState(
                    linkingUserId.Value,
                    provider,
                    request.Id,
                    redirectUri,
                    DateTime.UtcNow);

                if (!SamlLinkStateManager.TryAdd(relayState, samlLinkState))
                {
                    return StatusCode(StatusCodes.Status409Conflict, "A SAML linking flow with the same state already exists.");
                }
            }

            string startUrl = request.GetRedirectUrl(config.SamlEndpoint.Trim(), relayState);
            if (returnStartUrl)
            {
                return Content(startUrl, MediaTypeNames.Text.Plain);
            }

            return Redirect(startUrl);
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds a SAML configuration. If the provider already exists, overwrite it.
    /// </summary>
    /// <param name="provider">The provider name to add.</param>
    /// <param name="newConfig">The SAML configuration object (deserialized) from JSON.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SAML/Add/{provider}")]
    public OkResult SamlAdd(string provider, [FromBody] SamlConfig newConfig)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs[provider] = newConfig;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Deletes a provider from the configuration with a given ID.
    /// </summary>
    /// <param name="provider">The ID of the provider to delete.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Del/{provider}")]
    public OkResult SamlDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Returns a list of all SAML providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>A list of all of the Saml providers available.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Get")]
    public ActionResult SamlProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("SAML/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SamlAuth(string provider, [FromBody] AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            bool isAdmin = false;
            bool liveTv = config.EnableLiveTv;
            bool liveTvManagement = config.EnableLiveTvManagement;
            var samlResponse = new Response(config.SamlCertificate, response.Data);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            HashSet<string> folders;
            if (config.EnabledFolders != null)
            {
                folders = new HashSet<string>(config.EnabledFolders);
            }
            else
            {
                folders = new HashSet<string>();
            }

            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                if (config.AdminRoles != null)
                {
                    foreach (string allowedRole in config.AdminRoles)
                    {
                        if (allowedRole.Equals(role))
                        {
                            isAdmin = true;
                        }
                    }
                }

                if (config.EnableFolderRoles)
                {
                    if (config.FolderRoleMapping != null)
                    {
                        foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                        {
                            if (folderRoleMap.Role.Equals(role))
                            {
                                folders.UnionWith(folderRoleMap.Folders);
                            }
                        }
                    }
                }

                if (config.EnableLiveTvRoles)
                {
                    if (config.LiveTvRoles != null)
                    {
                        foreach (string allowedLiveTvRole in config.LiveTvRoles)
                        {
                            if (allowedLiveTvRole.Equals(role))
                            {
                                liveTv = true;
                            }
                        }
                    }

                    if (config.LiveTvManagementRoles != null)
                    {
                        foreach (string allowedLiveTvManagementRole in config.LiveTvManagementRoles)
                        {
                            if (allowedLiveTvManagementRole.Equals(role))
                            {
                                liveTvManagement = true;
                            }
                        }
                    }
                }
            }

            Guid? userId = await CreateCanonicalLinkAndUserIfNotExist("saml", provider, samlResponse.GetNameID(), samlResponse.GetNameID());
            if (userId is null)
            {
                return Conflict(UsernameTakenMessage);
            }

            var authenticationResult = await Authenticate(
                userId.Value,
                isAdmin,
                config.EnableAuthorization,
                config.EnableAllFolders,
                folders.ToArray(),
                liveTv,
                liveTvManagement,
                response,
                config.DefaultProvider?.Trim(),
                null,
                config.PreserveAdminPermissions)
                .ConfigureAwait(false);
            return Ok(authenticationResult);
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// Removes a user from SSO auth and switches it back to another auth provider. Requires administrator privileges.
    /// </summary>
    /// <param name="username">The username to switch to the new provider.</param>
    /// <param name="provider">The new provider to switch to.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Unregister/{username}")]
    public async Task<ActionResult> Unregister(string username, [FromBody] string provider)
    {
        User user = _userManager.GetUserByName(username);

        if (user == null)
        {
            return NotFound("No matching user found");
        }

        user.AuthenticationProviderId = provider;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        return Ok();
    }

    private SerializableDictionary<string, Guid> GetCanonicalLinks(string mode, string provider)
    {
        SerializableDictionary<string, Guid> links = null;

        switch (mode.ToLower())
        {
            case "saml":
                links = SSOPlugin.Instance.Configuration.SamlConfigs[provider].CanonicalLinks;
                break;
            case "oid":
                links = SSOPlugin.Instance.Configuration.OidConfigs[provider].CanonicalLinks;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        if (links == null)
        {
            links = new SerializableDictionary<string, Guid>();
        }

        return links;
    }

    /// <summary>
    /// Resolves (or provisions) the Jellyfin user for a provider identity.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The provider the identity belongs to.</param>
    /// <param name="canonicalId">The provider's stable identifier for the identity (OIDC sub, SAML NameID).</param>
    /// <param name="canonicalName">The username the provider reports for the identity.</param>
    /// <returns>
    /// The Jellyfin user id, or null when the username is already taken by a local account
    /// this provider is not allowed to adopt.
    /// </returns>
    private async Task<Guid?> CreateCanonicalLinkAndUserIfNotExist(string mode, string provider, string canonicalId, string canonicalName)
    {
        User user = null;

        // First try to get the user by its id in case it was already registered before
        Guid userId = Guid.Empty;
        try
        {
            userId = GetCanonicalLink(mode, provider, canonicalId);
        }
        catch (KeyNotFoundException)
        {
            userId = Guid.Empty;
        }

        // If a link exists, try to resolve the user it points to.
        if (userId != Guid.Empty)
        {
            user = _userManager.GetUserById(userId);

            // The link points to a user that no longer exists. This happens when the user's
            // GUID changes out from under us (e.g. across a Jellyfin database migration such
            // as the one in 10.11). Drop the stale link so we fall back to a name lookup
            // below instead of incorrectly trying to create a user that already exists.
            if (user == null)
            {
                _logger.LogWarning($"SSO canonical link for {canonicalName} points to missing user {userId}; removing stale link");
                var staleLinks = GetCanonicalLinks(mode, provider);
                staleLinks.Remove(canonicalId);
                UpdateCanonicalLinkConfig(staleLinks, mode, provider);
            }
        }

        // Releases before 6.0 keyed links by username. Honour such a link so nobody loses
        // their account on upgrade; MigrateLegacyUsernameLink rekeys it below.
        if (user == null && !string.Equals(canonicalId, canonicalName, StringComparison.Ordinal))
        {
            try
            {
                user = _userManager.GetUserById(GetCanonicalLink(mode, provider, canonicalName));
            }
            catch (KeyNotFoundException)
            {
                user = null;
            }
        }

        // No (valid) link found? Adopt the local account of the same name, unless the
        // provider is configured not to: adoption hands whoever controls the username claim
        // any local account of that name, administrators included.
        if (user == null)
        {
            var existing = _userManager.GetUserByName(canonicalName);
            if (existing != null)
            {
                if (UsernameAdoptionDisabled(mode, provider))
                {
                    _logger.LogWarning(
                        "Refusing the login for {Username} on {Provider}: a local account of that name exists but is not linked to this provider, and username account adoption is disabled",
                        canonicalName,
                        provider);
                    return null;
                }

                _logger.LogWarning(
                    "Adopting existing local account {Username} for a first login on {Provider}; the provider's username claim is authoritative for this provider",
                    canonicalName,
                    provider);
                user = existing;
            }
        }

        if (user == null)
        {
            _logger.LogInformation($"SSO user {canonicalName} ({canonicalId}) doesn't exist, creating...");
            user = await _userManager.CreateUserAsync(canonicalName).ConfigureAwait(false);
            user.AuthenticationProviderId = GetType().FullName;
            // https://jonathancrozier.com/blog/how-to-generate-a-cryptographically-secure-random-string-in-dot-net-with-c-sharp
            user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

            // Strip Jellyfin's default library permissions exactly once, on creation. New SSO
            // users must not inherit access to every folder: either the provider's role mapping
            // will set their folders below, or they default to none as the config text promises.
            // Persist via UpdatePolicyAsync (Jellyfin 10.11+/12 no longer save permissions through
            // UpdateUserAsync, jellyfin/jellyfin#16298).
            var newUserPolicy = _userManager.GetUserDto(user).Policy;
            newUserPolicy.EnableAllFolders = false;
            newUserPolicy.EnabledFolders = Array.Empty<Guid>();
            await _userManager.UpdatePolicyAsync(user.Id, newUserPolicy).ConfigureAwait(false);
            user = _userManager.GetUserById(user.Id);

            // Make sure there aren't any trailing existing links
            var links = GetCanonicalLinks(mode, provider);
            links.Remove(canonicalId);
            UpdateCanonicalLinkConfig(links, mode, provider);
        }

        try
        {
            userId = GetCanonicalLink(mode, provider, canonicalId);
        }
        catch (KeyNotFoundException)
        {
            userId = Guid.Empty;
        }

        // Create the link if it is missing, or repair it if it points at the wrong user.
        if (userId != user.Id)
        {
            _logger.LogInformation("SSO user link doesn't exist or is outdated, creating...");
            userId = user.Id;
            CreateCanonicalLink(mode, provider, userId, canonicalId);
        }

        MigrateLegacyUsernameLink(mode, provider, canonicalId, user);

        return userId;
    }

    /// <summary>
    /// Loads an avatar either from an inline data: URL or by downloading it.
    /// </summary>
    /// <param name="avatarUrl">The avatar URL from the provider.</param>
    /// <returns>The image bytes and their content type.</returns>
    private async Task<(MemoryStream Stream, string ContentType)> LoadAvatar(string avatarUrl)
    {
        if (avatarUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            // Providers such as Kanidm and Pocket ID hand the picture out inline.
            var base64Data = avatarUrl.Substring(avatarUrl.IndexOf(',', StringComparison.Ordinal) + 1);
            var contentType = avatarUrl.Substring(5, avatarUrl.IndexOf(';', StringComparison.Ordinal) - 5);
            return (new MemoryStream(Convert.FromBase64String(base64Data)), contentType);
        }

        using var client = _httpClientFactory.CreateClient();

        System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
        System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
        string version = fvi.FileVersion;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SSO-Auth +{version} (https://github.com/9p4/jellyfin-plugin-sso)");

        using var avatarResponse = await client.GetAsync(avatarUrl).ConfigureAwait(false);

        if (!avatarResponse.Content.Headers.TryGetValues("content-type", out var contentTypeList))
        {
            throw new Exception("Cannot get Content-Type of image : " + avatarUrl);
        }

        var downloadedContentType = contentTypeList.First();
        if (!downloadedContentType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("Content type of avatar URL is not an image, got :  " + downloadedContentType);
        }

        return (new MemoryStream(await avatarResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false)), downloadedContentType);
    }

    private static bool UsernameAdoptionDisabled(string mode, string provider)
    {
        return mode switch
        {
            "oid" => SSOPlugin.Instance.Configuration.OidConfigs[provider].DisableUsernameAccountAdoption,
            "saml" => SSOPlugin.Instance.Configuration.SamlConfigs[provider].DisableUsernameAccountAdoption,
            _ => throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'"),
        };
    }

    private void MigrateLegacyUsernameLink(string mode, string provider, string canonicalId, User user)
    {
        var links = GetCanonicalLinks(mode, provider);
        var legacyKeys = links
            .Where(link => link.Value == user.Id && !string.Equals(link.Key, canonicalId, StringComparison.Ordinal))
            .Select(link => link.Key)
            .ToList();

        if (legacyKeys.Count == 0)
        {
            return;
        }

        foreach (var legacyKey in legacyKeys)
        {
            _logger.LogInformation("Removing legacy username-keyed SSO link {LegacyKey} for user {UserId}", legacyKey, user.Id);
            links.Remove(legacyKey);
        }

        UpdateCanonicalLinkConfig(links, mode, provider);
    }

    private Guid GetCanonicalLink(string mode, string provider, string canonicalId)
    {
        var links = GetCanonicalLinks(mode, provider);
        return links[canonicalId];
    }

    /// <summary>
    /// Create a canonical link for a given user. Must be performed by the user being changed, or admin.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider to link to a jellyfin account.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to link to the provider.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpPost("{mode}/Link/{provider}/{jellyfinUserId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> AddCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromBody] AuthResponse authResponse)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to link SSO providers.");
        }

        switch (mode.ToLower())
        {
            case "saml":
                return SamlLink(provider, jellyfinUserId, authResponse);
            case "oid":
                return OidLink(provider, jellyfinUserId, authResponse);
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }
    }

    /// <summary>
    /// Unregisters a given mapping from id within provider to user.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider from which the link should be removed.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to unlink from the provider.</param>
    /// <param name="canonicalName">The user ID within jellyfin to unlink.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpDelete("{mode}/Link/{provider}/{jellyfinUserId}/{canonicalName}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> DeleteCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromRoute] string canonicalName)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Current user is not allowed to unlink SSO providers for user ID.");
        }

        var links = GetCanonicalLinks(mode, provider);

        if (!links.TryGetValue(canonicalName, out var linkedId))
        {
            return NotFound("No link is registered for that canonical name.");
        }

        if (linkedId != jellyfinUserId)
        {
            return StatusCode(StatusCodes.Status409Conflict, "jellyfin UID does not match id registered to that canonical name.");
        }

        links.Remove(canonicalName);

        return UpdateCanonicalLinkConfig(links, mode, provider);
    }

    /// <summary>
    /// Gets all the saml links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("saml/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetSamlLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.SamlConfigs;

        foreach (var providerName in providerList.Keys)
        {
            var canonLinks = providerList[providerName].CanonicalLinks;
            var canonKeys = from link in canonLinks where link.Value == jellyfinUserId select link.Key;
            mappings[providerName] = canonKeys;
        }

        return mappings;
    }

    /// <summary>
    /// Gets all the oid links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("oid/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetOidLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();

        foreach (var provider in SSOPlugin.Instance.Configuration.OidConfigs)
        {
            // Materialised rather than left lazy: a deferred query is only evaluated while
            // the response is serialised, by which point the configuration it reads may
            // have been replaced by a concurrent update.
            mappings[provider.Key] = provider.Value.CanonicalLinks
                .Where(link => link.Value == jellyfinUserId)
                .Select(link => link.Key)
                .ToList();
        }

        return mappings;
    }

    /// <summary>
    /// Validate a saml link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">
    ///   The ID of the account to be linked to the provider.
    ///   Must be performed by this user, or an admin.
    /// </param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private ActionResult SamlLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        var samlResponse = new Response(config.SamlCertificate, response.Data);

        if (!samlResponse.IsValid())
        {
            return Problem("Invalid SAML signature");
        }

        string providerUserId = samlResponse.GetNameID();

        return CreateCanonicalLink("saml", provider, jellyfinUserId, providerUserId);
    }

    /// <summary>
    /// Validate an OIDC link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">The Jellyfin user to link the provider identity to.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private ActionResult OidLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (!string.IsNullOrEmpty(response.Data)
            && StateManager.TryGetValue(response.Data, out var timedState)
            && timedState.Valid
            && !IsAuthorizationStateExpired(timedState.Created)
            && string.Equals(timedState.Provider, provider, StringComparison.Ordinal))
        {
            return CreateCanonicalLink("oid", provider, jellyfinUserId, timedState.Id);
        }

        return BadRequest("Invalid or expired authorization state.");
    }

    private ActionResult CreateCanonicalLink(string mode, string provider, [FromRoute] Guid jellyfinUserId, string providerUserId)
    {
        if (string.IsNullOrWhiteSpace(providerUserId))
        {
            return BadRequest("The SSO provider did not return a usable user identifier.");
        }

        SerializableDictionary<string, Guid> links = null;
        try
        {
            links = GetCanonicalLinks(mode, provider);
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (links.TryGetValue(providerUserId, out var existingUserId) && existingUserId != jellyfinUserId)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                "This SSO identity is already linked to another Jellyfin user.");
        }

        links[providerUserId] = jellyfinUserId;
        UpdateCanonicalLinkConfig(links, mode, provider);

        return NoContent();
    }

    private OkResult UpdateCanonicalLinkConfig(SerializableDictionary<string, Guid> links, string mode, string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        switch (mode.ToLower())
        {
            case "saml":
                configuration.SamlConfigs[provider].CanonicalLinks = links;
                break;
            case "oid":
                configuration.OidConfigs[provider].CanonicalLinks = links;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Authenticates the user with the given information.
    /// </summary>
    /// <param name="userId">The user id of the user to authenticate.</param>
    /// <param name="isAdmin">Determines whether this user is an administrator.</param>
    /// <param name="enableAuthorization">Determines whether RBAC is used for this user.</param>
    /// <param name="enableAllFolders">Determines whether all folders are enabled.</param>
    /// <param name="enabledFolders">Determines which folders should be enabled for this client.</param>
    /// <param name="enableLiveTv">Determines whether live TV access is allowed for this user.</param>
    /// <param name="enableLiveTvAdmin">Determines whether live TV can be managed by this user.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <param name="defaultProvider">The default provider of the user to be set after logging in.</param>
    /// <param name="avatarUrl">The new avatar url for the user.</param>
    /// <param name="preserveAdmin">When true, never demote an existing admin; only ever elevate.</param>
    private async Task<AuthenticationResult> Authenticate(Guid userId, bool isAdmin, bool enableAuthorization, bool enableAllFolders, string[] enabledFolders, bool enableLiveTv, bool enableLiveTvAdmin, AuthResponse authResponse, string defaultProvider, string avatarUrl, bool preserveAdmin)
    {
        User user = _userManager.GetUserById(userId);

        // Jellyfin 10.11's UpdateUserAsync no longer persists modified permission/preference rows,
        // so permissions must be written through UpdatePolicyAsync.
        var policy = _userManager.GetUserDto(user).Policy;

        // UpdatePolicyAsync re-adds the schedules; it needs untracked Id=0 copies.
        policy.AccessSchedules = policy.AccessSchedules
            .Select(schedule => new AccessSchedule(schedule.DayOfWeek, schedule.StartHour, schedule.EndHour, userId))
            .ToArray();

        // GetUserDto does not expose this permission, so carry it over or the round-trip resets it.
        policy.EnableLyricManagement = user.HasPermission(PermissionKind.EnableLyricManagement);

        if (enableAuthorization)
        {
            if (isAdmin)
            {
                _logger.LogInformation("User {Username} matched an admin role; granting IsAdministrator.", user.Username);
                policy.IsAdministrator = true;
            }
            else if (preserveAdmin && policy.IsAdministrator)
            {
                _logger.LogInformation("User {Username} did not match an admin role, but PreserveAdminPermissions is enabled; leaving IsAdministrator unchanged.", user.Username);
            }
            else
            {
                if (policy.IsAdministrator)
                {
                    _logger.LogWarning("User {Username} did not match an admin role; revoking IsAdministrator.", user.Username);
                }

                policy.IsAdministrator = false;
            }

            policy.EnableAllFolders = enableAllFolders;
            if (!enableAllFolders)
            {
                var folderIds = new List<Guid>();
                foreach (string folder in enabledFolders)
                {
                    if (Guid.TryParse(folder, out var folderId))
                    {
                        folderIds.Add(folderId);
                    }
                    else
                    {
                        _logger.LogWarning("Ignoring enabled folder {Folder}: not a valid folder id.", folder);
                    }
                }

                policy.EnabledFolders = folderIds.ToArray();
            }
        }

        policy.EnableLiveTvAccess = enableLiveTv;
        policy.EnableLiveTvManagement = enableLiveTvAdmin;

        if (!string.IsNullOrEmpty(defaultProvider))
        {
            policy.AuthenticationProviderId = defaultProvider;
            _logger.LogInformation("Set default login provider to " + defaultProvider);
        }

        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);

        // UpdatePolicyAsync replaced the cached user entity.
        user = _userManager.GetUserById(userId);

        if (avatarUrl is not null)
        {
            try
            {
                var avatar = await LoadAvatar(avatarUrl).ConfigureAwait(false);
                using var stream = avatar.Stream;
                var contentType = avatar.ContentType;
                var extension = contentType.Split("/").Last();

                if (user != null)
                {
                    var userDataPath =
                        Path.Combine(
                            _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath,
                            user.Username);
                    if (user.ProfileImage is not null)
                    {
                        await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
                    }

                    user.ProfileImage = new ImageInfo(Path.Combine(userDataPath, "profile" + extension));

                    await _providerManager.SaveImage(stream, contentType, user.ProfileImage.Path)
                        .ConfigureAwait(false);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to set the profile image from {AvatarUrl}", avatarUrl);
            }
        }

        var authRequest = new AuthenticationRequest();
        authRequest.UserId = user.Id;
        authRequest.Username = user.Username;
        authRequest.App = authResponse.AppName;
        authRequest.AppVersion = authResponse.AppVersion;
        authRequest.DeviceId = authResponse.DeviceID;
        authRequest.DeviceName = authResponse.DeviceName;
        _logger.LogInformation("Auth request created...");

        return await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs the discovery trust checks a provider has switched off.
    /// </summary>
    /// <remarks>
    /// Each of these exists for a real provider quirk, but they weaken how far the discovery
    /// document can be trusted, and nothing else in the plugin surfaces that they are set.
    /// </remarks>
    /// <param name="config">The provider configuration.</param>
    private void WarnAboutRelaxedDiscovery(OidConfig config)
    {
        if (config.DisableHttps)
        {
            _logger.LogWarning("HTTPS is not required for OpenID discovery on {Endpoint}; the discovery document and tokens may travel in the clear", config.OidEndpoint?.Trim());
        }

        if (config.DoNotValidateIssuerName)
        {
            _logger.LogWarning("Issuer name validation is disabled for {Endpoint}; the discovery document is no longer checked against the configured issuer", config.OidEndpoint?.Trim());
        }

        if (config.DoNotValidateEndpoints)
        {
            _logger.LogWarning("Endpoint validation is disabled for {Endpoint}; the provider may advertise endpoints on unrelated hosts", config.OidEndpoint?.Trim());
        }
    }

    private void Invalidate()
    {
        foreach (var kvp in StateManager)
        {
            if (IsAuthorizationStateExpired(kvp.Value.Created))
            {
                StateManager.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in SamlLinkStateManager)
        {
            if (IsAuthorizationStateExpired(kvp.Value.Created))
            {
                SamlLinkStateManager.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static bool IsAuthorizationStateExpired(DateTime created)
    {
        return DateTime.UtcNow.Subtract(created.ToUniversalTime()) > AuthorizationStateLifetime;
    }

    private string GetRequestBase(string schemeOverride = null, int? portOverride = null)
    {
        int requestPort;

        if (portOverride != null)
        {
            requestPort = portOverride.Value;
        }
        else
        {
            requestPort = Request.Host.Port ?? -1;
        }

        if ((requestPort == 80 && string.Equals(Request.Scheme, "http", StringComparison.OrdinalIgnoreCase)) || (requestPort == 443 && string.Equals(Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            requestPort = -1;
        }

        if (schemeOverride != "http" && schemeOverride != "https")
        {
            schemeOverride = null;
        }

        return new UriBuilder
        {
            Scheme = schemeOverride ?? Request.Scheme,
            Host = Request.Host.Host,
            Port = requestPort,
            Path = Request.PathBase
        }.ToString().TrimEnd('/');
    }

    private ContentResult ReturnError(int code, string message)
    {
        var errorResult = new ContentResult();
        errorResult.Content = message;
        errorResult.ContentType = MediaTypeNames.Text.Plain;
        errorResult.StatusCode = code;
        return errorResult;
    }
}

/// <summary>
/// The request body for the device code flow authentication endpoint.
/// </summary>
public class DeviceAuthRequest
{
    /// <summary>
    /// Gets or sets the id_token obtained from the device code token endpoint.
    /// </summary>
    public string IdToken { get; set; }

    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }
}

/// <summary>
/// The data the client should pass back to the API.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the auth data of the client (for authorizing the response).
    /// </summary>
    public string Data { get; set; }
}

/// <summary>
/// A manager for OpenID to manage the state of the clients.
/// </summary>
public class TimedAuthorizeState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAuthorizeState"/> class.
    /// </summary>
    /// <param name="state">The AuthorizeState to time.</param>
    /// <param name="created">When this state was created.</param>
    // so im gonna keep it a buck right now in that i really have no idea what is going on
    public TimedAuthorizeState(AuthorizeState state, DateTime created)
    {
        State = state;
        Created = created;
        Valid = false;
        Admin = false;
        IsLinking = false;
        LinkingUserId = null;
        Provider = null;
        EnableLiveTv = false;
        EnableLiveTvManagement = false;
        AvatarURL = null;
    }

    /// <summary>
    /// Gets or sets the Authorization State of the client.
    /// </summary>
    public AuthorizeState State { get; set; }

    /// <summary>
    /// Gets or sets when this object was created to time it out.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is valid.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets the user name tied to the state.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the user id tied to the state.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool Admin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the state is
    /// tied to a linking flow (instead of a login flow).
    /// </summary>
    public bool IsLinking { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin Quick Connect code to prefill after authentication.
    /// </summary>
    public string QuickConnectCode { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user that authenticated the start of a linking flow.
    /// </summary>
    public Guid? LinkingUserId { get; set; }

    /// <summary>
    /// Gets or sets the OIDC provider that owns this authorization state.
    /// </summary>
    public string Provider { get; set; }

    /// <summary>
    /// Gets or sets the folders the user is allowed access to.
    /// </summary>
    public HashSet<string> Folders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to view live TV.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to manage live TV.
    /// </summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>
    /// Gets or sets the user avatar url.
    /// </summary>
    public string AvatarURL { get; set; }
}

/// <summary>
/// Stores the authenticated Jellyfin side of an in-progress SAML linking transaction.
/// </summary>
public sealed class TimedSamlLinkState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimedSamlLinkState"/> class.
    /// </summary>
    /// <param name="jellyfinUserId">The authenticated Jellyfin user.</param>
    /// <param name="provider">The SAML provider.</param>
    /// <param name="requestId">The SAML authentication request ID.</param>
    /// <param name="recipient">The assertion consumer service URL for the request.</param>
    /// <param name="created">When the transaction was created.</param>
    public TimedSamlLinkState(Guid jellyfinUserId, string provider, string requestId, string recipient, DateTime created)
    {
        JellyfinUserId = jellyfinUserId;
        Provider = provider;
        RequestId = requestId;
        Recipient = recipient;
        Created = created;
    }

    /// <summary>
    /// Gets the authenticated Jellyfin user.
    /// </summary>
    public Guid JellyfinUserId { get; }

    /// <summary>
    /// Gets the SAML provider.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// Gets the SAML authentication request ID.
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// Gets the assertion consumer service URL for the request.
    /// </summary>
    public string Recipient { get; }

    /// <summary>
    /// Gets when the transaction was created.
    /// </summary>
    public DateTime Created { get; }
}
