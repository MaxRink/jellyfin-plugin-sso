using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Tests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;

namespace SSO_Auth.Tests;

public partial class OidDeviceAuthTests
{
    [Fact]
    public async Task PasswordlessRepairPreservesRoutingAndExistingPasswords()
    {
        _testUser.Password = null;
        var routing = _testUser.AuthenticationProviderId;
        Assert.True(await PasswordlessAccountSealer.SealAsync(_testUser, _userManager.Object, new FakeCryptoProvider()));
        Assert.False(string.IsNullOrEmpty(_testUser.Password));
        Assert.Equal(routing, _testUser.AuthenticationProviderId);
        var password = _testUser.Password;
        Assert.False(await PasswordlessAccountSealer.SealAsync(_testUser, _userManager.Object, new FakeCryptoProvider()));
        Assert.Equal(password, _testUser.Password);
        _userManager.Verify(m => m.UpdateUserAsync(_testUser), Times.Once);
    }

    [Fact]
    public async Task FailedNewAccountPasswordWriteRollsBackAccount()
    {
        _testUser.Password = null;
        _userManager.Setup(m => m.UpdateUserAsync(_testUser)).ThrowsAsync(new IOException("write failed"));
        await Assert.ThrowsAsync<IOException>(() => PasswordlessAccountSealer.SealNewAccountAsync(
            _testUser, _userManager.Object, new FakeCryptoProvider(), NullLogger.Instance));
        _userManager.Verify(m => m.DeleteUserAsync(_testUserId), Times.Once);
    }

    [Fact]
    public async Task StartupRepairVisitsOnlyLinkedAccountsAndDeduplicatesThem()
    {
        _testUser.Password = null;
        var config = new PluginConfiguration();
        config.OidConfigs["oid"] = new OidConfig();
        config.OidConfigs["oid"].CanonicalLinks["sub"] = _testUserId;
        config.SamlConfigs["saml"] = new SamlConfig();
        config.SamlConfigs["saml"].CanonicalLinks["name"] = _testUserId;
        config.SamlConfigs["saml"].CanonicalLinks["deleted"] = Guid.NewGuid();
        var sweep = new PasswordlessLinkedAccountSweep(config, _userManager.Object, new FakeCryptoProvider(), NullLogger.Instance);
        Assert.Equal(1, await sweep.SweepAsync());
        Assert.Equal(0, await sweep.SweepAsync());
        _userManager.Verify(m => m.UpdateUserAsync(_testUser), Times.Once);
    }

    [Fact]
    public void DebugStatesDoNotDiscloseProtocolSecretsOrIdentity()
    {
        var states = (ConcurrentDictionary<string, TimedAuthorizeState>)typeof(SSOController)
            .GetField("StateManager", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var key = Guid.NewGuid().ToString();
        states[key] = new TimedAuthorizeState(new AuthorizeState { State = key, CodeVerifier = "secret-verifier" }, DateTime.UtcNow)
        { Provider = Provider, Username = "private-username", AvatarURL = "private-avatar" };
        try
        {
            var json = JsonConvert.SerializeObject(Assert.IsType<OkObjectResult>(_controller.OidStates()).Value);
            Assert.DoesNotContain(key, json);
            Assert.DoesNotContain("secret-", json);
            Assert.DoesNotContain("private-", json);
            Assert.Contains(Provider, json);
        }
        finally { states.TryRemove(key, out _); }
    }

    [Fact]
    public void MixedRoleArraysKeepOnlyStringRoles()
    {
        var method = typeof(SSOController).GetMethod("GetRolesFromClaimPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var roles = (List<string>)method.Invoke(null, new object[]
        { new Claim("realm_access", "{\"roles\":[\"users\",42,null,{},\"admins\"]}"), new[] { "realm_access", "roles" } })!;
        Assert.Equal(new[] { "users", "admins" }, roles);
    }

    [Fact]
    public async Task SamlRawAssertionsCannotBypassTheCallback()
    {
        var fixture = SamlTestFactory.Create(audience: "jellyfin", nameId: "testuser");
        SSOPlugin.Instance.Configuration.SamlConfigs[Provider] = new SamlConfig
        { Enabled = true, SamlClientId = "jellyfin", SamlCertificate = fixture.CertificateBase64 };
        Assert.IsType<BadRequestObjectResult>(await _controller.SamlAuth(Provider, new AuthResponse { Data = fixture.EncodeResponse() }));
        _sessionManager.Verify(m => m.AuthenticateDirect(It.IsAny<MediaBrowser.Controller.Session.AuthenticationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamlHandoffIsSingleUseAndRechecksRoleAllowList(bool denyRole)
    {
        var fixture = SamlTestFactory.Create(audience: "jellyfin", nameId: "testuser", role: "users");
        var config = new SamlConfig { Enabled = true, SamlClientId = "jellyfin", SamlCertificate = fixture.CertificateBase64,
            Roles = denyRole ? new[] { "administrators" } : new[] { "users" } };
        config.CanonicalLinks["testuser"] = _testUserId;
        SSOPlugin.Instance.Configuration.SamlConfigs[Provider] = config;
        var states = (ConcurrentDictionary<string, (string Provider, string Response, DateTime Created)>)typeof(SSOController)
            .GetField("SamlAuthStates", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var token = Guid.NewGuid().ToString();
        states[token] = (Provider, fixture.EncodeResponse(), DateTime.UtcNow);
        var result = await _controller.SamlAuth(Provider, new AuthResponse { Data = token });
        if (denyRole) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<OkObjectResult>(result);
        Assert.IsType<BadRequestObjectResult>(await _controller.SamlAuth(Provider, new AuthResponse { Data = token }));
        _sessionManager.Verify(m => m.AuthenticateDirect(It.IsAny<MediaBrowser.Controller.Session.AuthenticationRequest>()), denyRole ? Times.Never() : Times.Once());
    }
    [Fact]
    public async Task ManagedPermissionsPreserveOnlyUnmanagedFolderGrants()
    {
        var manual = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var granted = Guid.NewGuid();
        var policy = new MediaBrowser.Model.Users.UserPolicy { EnabledFolders = new[] { manual, revoked }, EnableLiveTvAccess = true };
        _testUser.Password = "existing";
        _userManager.Setup(m => m.GetUserDto(_testUser, It.IsAny<string>())).Returns(new MediaBrowser.Model.Dto.UserDto { Policy = policy });
        var method = typeof(SSOController).GetMethod("Authenticate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_controller, new object?[] { _testUserId, false, true, false,
            new[] { granted.ToString() }, false, false, new AuthResponse(), null, null, false, false, true,
            new[] { revoked.ToString(), granted.ToString() } })!;
        Assert.Contains(manual, policy.EnabledFolders);
        Assert.Contains(granted, policy.EnabledFolders);
        Assert.DoesNotContain(revoked, policy.EnabledFolders);
        Assert.False(policy.EnableContentDownloading);
        Assert.False(policy.EnableLiveTvAccess);
    }

    [Fact]
    public async Task UnmanagedPermissionsStayUnchangedIncludingLiveTv()
    {
        var policy = new MediaBrowser.Model.Users.UserPolicy { EnableLiveTvAccess = true, EnableLiveTvManagement = true, EnableContentDownloading = true };
        _testUser.Password = "existing";
        _userManager.Setup(m => m.GetUserDto(_testUser, It.IsAny<string>())).Returns(new MediaBrowser.Model.Dto.UserDto { Policy = policy });
        var method = typeof(SSOController).GetMethod("Authenticate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_controller, new object?[] { _testUserId, false, false, false,
            Array.Empty<string>(), false, false, new AuthResponse(), null, null, false, false, false, null })!;
        Assert.True(policy.EnableLiveTvAccess);
        Assert.True(policy.EnableLiveTvManagement);
        Assert.True(policy.EnableContentDownloading);
    }

    [Fact]
    public async Task LoggingInWithOneSubjectKeepsOtherExplicitLinks()
    {
        var config = new OidConfig();
        config.CanonicalLinks["subject-one"] = _testUserId;
        config.CanonicalLinks["subject-two"] = _testUserId;
        SSOPlugin.Instance.Configuration.OidConfigs[Provider] = config;
        var method = typeof(SSOController).GetMethod("CreateCanonicalLinkAndUserIfNotExist", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<Guid?>)method.Invoke(_controller, new object[] { "oid", Provider, "subject-one", "testuser" })!;
        Assert.Equal(_testUserId, result);
        Assert.Equal(_testUserId, config.CanonicalLinks["subject-two"]);
    }

}
