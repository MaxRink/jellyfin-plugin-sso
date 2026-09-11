using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SSO_Auth.Auth;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth;

/// <summary>
/// The SSO plugin class.
/// </summary>
public class SSOPlugin : BasePlugin<PluginConfiguration>, IPlugin, IHasWebPages
{
    private readonly IUserManager _userManager;
    private readonly ILogger<SSOPlugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Internal Jellyfin interface for the ApplicationPath.</param>
    /// <param name="xmlSerializer">Internal Jellyfin interface for the XML information.</param>
    /// <param name="userManager">The user manager, used to apply the SSO-only login setting.</param>
    /// <param name="logger">The logger.</param>
    public SSOPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IUserManager userManager, ILogger<SSOPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _userManager = userManager;
        _logger = logger;
        Instance = this;
    }

    /// <summary>
    /// Gets the instance of the SSO plugin.
    /// </summary>
    public static SSOPlugin Instance { get; private set; }

    /// <summary>
    /// Gets the name of the SSO plugin.
    /// </summary>
    public override string Name => "SSO-Auth";

    /// <summary>
    /// Gets the GUID of the SSO plugin.
    /// </summary>
    public override Guid Id => Guid.Parse("505ce9d1-d916-42fa-86ca-673ef241d7df");

    /// <summary>
    /// Saves the configuration, and applies the SSO-only login setting to existing accounts when it
    /// has changed.
    /// </summary>
    /// <param name="configuration">The configuration to save.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var before = Configuration;
        var wasEnforcing = before.EnforceSsoOnly;
        var previousExemptions = before.SsoOnlyExemptUsernames ?? Array.Empty<string>();

        base.UpdateConfiguration(configuration);

        var exemptionsChanged = !previousExemptions.SequenceEqual(Configuration.SsoOnlyExemptUsernames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (wasEnforcing == Configuration.EnforceSsoOnly && !exemptionsChanged)
        {
            // Nothing to sweep. Checked because the login flow also saves the configuration (to
            // remember the callback path style), and a sweep there would run on every login.
            return;
        }

        foreach (var user in _userManager.GetUsers())
        {
            try
            {
                SsoOnlyEnforcer.EnforceAsync(_userManager, _logger, user).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not apply the SSO-only login setting to {Username}", user.Username);
            }
        }
    }

    /// <summary>
    /// Returns the available internal web pages of this plugin.
    /// </summary>
    /// <returns>A list of internal webpages in this application.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "SSO Authentication",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "login"
            },
            new PluginPageInfo
            {
                Name = Name + ".js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.config.js"
            },
            new PluginPageInfo
            {
                Name = Name + ".css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = Name + "-linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = Name + "-linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
            new PluginPageInfo
            {
                Name = Name + "-linking.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.linking.css"
            },
        };
    }

    /// <summary>
    /// Returns the available user views for this plugin.
    /// </summary>
    /// <returns>A list of user views for this plugin.</returns>
    public IEnumerable<PluginPageInfo> GetViews()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "style.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = "linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = "linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
            new PluginPageInfo
            {
                Name = "ApiClient.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.apiClient.js"
            },
            new PluginPageInfo
            {
                Name = "emby-restyle.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.emby-restyle.css"
            },
            new PluginPageInfo
            {
                Name = "linking.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.linking.css"
            },
            new PluginPageInfo
            {
                Name = "logout",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.logout.html"
            },
        };
    }
}
