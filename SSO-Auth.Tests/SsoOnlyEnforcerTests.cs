using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Auth;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace SSO_Auth.Tests;

/// <summary>
/// Checks which accounts lose password login when SSO-only login is enforced.
/// </summary>
[Collection("PluginInstance")]
public class SsoOnlyEnforcerTests
{
    private static readonly string SsoOnlyProviderId = typeof(SsoOnlyAuthProvider).FullName!;
    private readonly Mock<IUserManager> _userManager = new();
    private readonly PluginConfiguration _config;

    public SsoOnlyEnforcerTests()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Directory.CreateDirectory(appPaths.Object.DataPath);
        var xmlSerializer = new Mock<IXmlSerializer>();
        _config = new PluginConfiguration();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(_config);
        _ = new SSOPlugin(appPaths.Object, xmlSerializer.Object, _userManager.Object, new Mock<ILogger<SSOPlugin>>().Object);

        _userManager.Setup(m => m.GetUserDto(It.IsAny<User>(), It.IsAny<string>()))
            .Returns(() => new UserDto { Policy = new UserPolicy() });
    }

    private User Linked(string username = "max")
    {
        var user = new User(username, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Default") { Id = Guid.NewGuid() };
        SSOPlugin.Instance!.Configuration.OidConfigs["authelia"] = new OidConfig();
        SSOPlugin.Instance.Configuration.OidConfigs["authelia"].CanonicalLinks["max-sub"] = user.Id;
        return user;
    }

    [Fact]
    public async Task LocksAnAccountThatHasAnSsoLink()
    {
        UserPolicy? saved = null;
        _userManager.Setup(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()))
            .Callback<Guid, UserPolicy>((_, policy) => saved = policy).Returns(Task.CompletedTask);
        SSOPlugin.Instance!.Configuration.EnforceSsoOnly = true;

        await SsoOnlyEnforcer.EnforceAsync(_userManager.Object, NullLogger.Instance, Linked());

        Assert.Equal(SsoOnlyProviderId, saved?.AuthenticationProviderId);
    }

    [Fact]
    public async Task LeavesAnAccountWithoutAnSsoLinkAlone()
    {
        SSOPlugin.Instance!.Configuration.EnforceSsoOnly = true;
        var unlinked = new User("breakglass", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Default") { Id = Guid.NewGuid() };

        await SsoOnlyEnforcer.EnforceAsync(_userManager.Object, NullLogger.Instance, unlinked);

        _userManager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
    }

    [Fact]
    public async Task LeavesAnExemptAccountAlone()
    {
        SSOPlugin.Instance!.Configuration.EnforceSsoOnly = true;
        SSOPlugin.Instance.Configuration.SsoOnlyExemptUsernames = new[] { "MAX" };

        await SsoOnlyEnforcer.EnforceAsync(_userManager.Object, NullLogger.Instance, Linked());

        _userManager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
    }

    [Fact]
    public async Task RestoresPasswordLoginWhenTheSettingIsSwitchedOff()
    {
        UserPolicy? saved = null;
        _userManager.Setup(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()))
            .Callback<Guid, UserPolicy>((_, policy) => saved = policy).Returns(Task.CompletedTask);
        SSOPlugin.Instance!.Configuration.EnforceSsoOnly = false;
        var locked = Linked();
        locked.AuthenticationProviderId = SsoOnlyProviderId;

        await SsoOnlyEnforcer.EnforceAsync(_userManager.Object, NullLogger.Instance, locked);

        Assert.Equal(SsoOnlyEnforcer.DefaultAuthProviderId, saved?.AuthenticationProviderId);
    }
}
