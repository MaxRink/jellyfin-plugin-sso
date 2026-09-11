using System.Security.Cryptography;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using JellyfinCryptoProvider = MediaBrowser.Model.Cryptography.ICryptoProvider;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Newtonsoft.Json;
using SSO_Auth.Tests.Helpers;

namespace SSO_Auth.Tests;

/// <summary>
/// Tests for the OID device code flow authentication endpoint.
/// Each test gets a fresh SSOPlugin instance so state doesn't bleed between tests.
/// </summary>
[Collection("PluginInstance")]
public class OidDeviceAuthTests : IDisposable
{
    private const string Provider = "test-provider";
    private const string Issuer = "https://auth.example.com";
    private const string ClientId = "jellyfin-client";
    private const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";
    private const string JwksUrl = Issuer + "/.well-known/jwks.json";

    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly SSOController _controller;
    private readonly Mock<IUserManager> _userManager;
    private readonly Mock<ISessionManager> _sessionManager;
    private readonly User _testUser;
    private readonly Guid _testUserId = Guid.NewGuid();
    private readonly string _tempDir;

    public OidDeviceAuthTests()
    {
        _tempDir = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };

        // Bootstrap the static singleton so the controller can read OidConfigs.
        // xUnit creates a new test class instance per test, so this runs fresh each time.
        var appPaths = new Mock<IApplicationPaths>();
        // BasePlugin accesses several path properties in its ctor (DataPath, PluginConfigurationsPath, etc.)
        // so default all string returns to the temp dir to avoid NullReferenceExceptions.
        appPaths.SetReturnsDefault(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _ = new SSOPlugin(appPaths.Object, xmlSerializer.Object, new Mock<IUserManager>().Object, new Mock<ILogger<SSOPlugin>>().Object);

        _testUser = new User("testuser", "SSO-Auth", "Default") { Id = _testUserId };

        _userManager = new Mock<IUserManager>();
        _userManager.Setup(m => m.GetUserByName("testuser")).Returns((User?)null);
        _userManager.Setup(m => m.CreateUserAsync("testuser")).ReturnsAsync(_testUser);
        _userManager.Setup(m => m.GetUserById(_testUserId)).Returns(_testUser);
        _userManager.Setup(m => m.GetUserDto(It.IsAny<User>(), It.IsAny<string>()))
            .Returns(() => new UserDto { Policy = new UserPolicy() });
        _userManager.Setup(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>())).Returns(Task.CompletedTask);
        _userManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);

        _sessionManager = new Mock<ISessionManager>();
        _sessionManager
            .Setup(m => m.AuthenticateDirect(It.IsAny<AuthenticationRequest>()))
            .ReturnsAsync(new AuthenticationResult { ServerId = "test-server" });

        var logger = new Mock<ILogger<SSOController>>();
        var loggerFactory = new Mock<ILoggerFactory>();
        var authContext = new Mock<IAuthorizationContext>();
        var providerManager = new Mock<IProviderManager>();
        var serverConfig = new Mock<IServerConfigurationManager>();
        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(x => x.UserConfigurationDirectoryPath).Returns(_tempDir);
        serverConfig.Setup(x => x.ApplicationPaths).Returns(serverAppPaths.Object);

        _controller = new SSOController(
            logger.Object,
            loggerFactory.Object,
            _sessionManager.Object,
            _userManager.Object,
            authContext.Object,
            new FakeCryptoProvider(),
            providerManager.Object,
            BuildHttpClientFactory(),
            serverConfig.Object);
    }

    // --- Helpers ---

    private IHttpClientFactory BuildHttpClientFactory(RSA? alternateKey = null)
    {
        var key = alternateKey != null
            ? new RsaSecurityKey(alternateKey) { KeyId = "test-key-1" }
            : _signingKey;

        var jwkSet = new JsonWebKeySet();
        jwkSet.Keys.Add(JsonWebKeyConverter.ConvertFromRSASecurityKey(key));

        var discovery = new { issuer = Issuer, jwks_uri = JwksUrl };
        var responses = new Dictionary<string, string>
        {
            [DiscoveryUrl] = JsonConvert.SerializeObject(discovery),
            [JwksUrl] = JsonConvert.SerializeObject(jwkSet),
        };

        var handler = new MockHttpMessageHandler(responses);
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        return factory.Object;
    }

    private string MakeToken(
        string? issuer = null,
        string? audience = null,
        string username = "testuser",
        string[]? roles = null,
        bool expired = false,
        RSA? signingKey = null)
    {
        var key = signingKey != null
            ? new RsaSecurityKey(signingKey) { KeyId = "test-key-1" }
            : _signingKey;

        var claims = new Dictionary<string, object>
        {
            ["preferred_username"] = username,
            ["sub"] = "sub-" + username,
        };
        if (roles is not null)
        {
            claims["roles"] = roles;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? ClientId,
            IssuedAt = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddMinutes(-1),
            NotBefore = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddMinutes(-1),
            Expires = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = claims,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private void SetConfig(OidConfig config)
    {
        SSOPlugin.Instance.Configuration.OidConfigs[Provider] = config;
    }

    private OidConfig EnabledConfig(
        string[]? roles = null,
        string[]? adminRoles = null,
        string roleClaim = "roles") => new OidConfig
    {
        OidEndpoint = Issuer,
        OidClientId = ClientId,
        Enabled = true,
        EnableAuthorization = true,
        EnableAllFolders = true,
        Roles = roles ?? Array.Empty<string>(),
        AdminRoles = adminRoles ?? Array.Empty<string>(),
        RoleClaim = roleClaim,
        EnableFolderRoles = false,
        EnableLiveTvRoles = false,
        EnableLiveTv = false,
        EnableLiveTvManagement = false,
    };

    private DeviceAuthRequest ValidRequest(string token) => new DeviceAuthRequest
    {
        IdToken = token,
        DeviceID = "device-1",
        DeviceName = "Test Device",
        AppName = "TestApp",
        AppVersion = "1.0",
    };

    // --- Tests ---

    [Fact]
    public async Task Returns400_WhenProviderNotFound()
    {
        var result = await _controller.OidDeviceAuth("nonexistent", ValidRequest(MakeToken()));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Returns400_WhenProviderDisabled()
    {
        SetConfig(new OidConfig { Enabled = false });
        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(MakeToken()));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Returns400_WhenIdTokenMissing()
    {
        SetConfig(EnabledConfig());
        var result = await _controller.OidDeviceAuth(Provider, new DeviceAuthRequest { DeviceID = "x" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Returns401_WhenTokenSignedWithWrongKey()
    {
        SetConfig(EnabledConfig());
        using var wrongKey = RSA.Create(2048);
        var token = MakeToken(signingKey: wrongKey);

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenTokenExpired()
    {
        SetConfig(EnabledConfig());
        var token = MakeToken(expired: true);

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenWrongAudience()
    {
        SetConfig(EnabledConfig());
        var token = MakeToken(audience: "wrong-client");

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenWrongIssuer()
    {
        SetConfig(EnabledConfig());
        var token = MakeToken(issuer: "https://evil.example.com");

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenUserLacksRequiredRole()
    {
        SetConfig(EnabledConfig(roles: new[] { "jellyfin-users" }));
        var token = MakeToken(roles: new[] { "some-other-role" });

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task Returns200_WhenValidTokenAndNoRoleRestriction()
    {
        SetConfig(EnabledConfig());
        var token = MakeToken();

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Returns200_WhenUserHasRequiredRole()
    {
        SetConfig(EnabledConfig(roles: new[] { "jellyfin-users" }));
        var token = MakeToken(roles: new[] { "jellyfin-users" });

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Returns200_WhenUserHasOneOfMultipleAcceptedRoles()
    {
        SetConfig(EnabledConfig(roles: new[] { "admins", "users" }));
        var token = MakeToken(roles: new[] { "users" });

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task FallsBackToSubClaim_WhenNoPreferredUsername()
    {
        SetConfig(EnabledConfig());
        var subUser = new User("sub-only-user", "SSO-Auth", "Default") { Id = Guid.NewGuid() };
        _userManager.Setup(m => m.GetUserByName("sub-only-user")).Returns((User?)null);
        _userManager.Setup(m => m.CreateUserAsync("sub-only-user")).ReturnsAsync(subUser);
        _userManager.Setup(m => m.GetUserById(subUser.Id)).Returns(subUser);

        // Token with no preferred_username claim
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = "sub-only-user" },
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        var result = await _controller.OidDeviceAuth(Provider, ValidRequest(token));

        Assert.IsType<OkObjectResult>(result);
        _userManager.Verify(m => m.CreateUserAsync("sub-only-user"), Times.Once);
    }

    [Fact]
    public async Task CallsAuthenticateDirect_WithCorrectDeviceInfo()
    {
        SetConfig(EnabledConfig());
        var request = new DeviceAuthRequest
        {
            IdToken = MakeToken(),
            DeviceID = "my-device-id",
            DeviceName = "My TV",
            AppName = "Jellyfin",
            AppVersion = "2.0",
        };

        await _controller.OidDeviceAuth(Provider, request);

        _sessionManager.Verify(m => m.AuthenticateDirect(
            It.Is<AuthenticationRequest>(r =>
                r.DeviceId == "my-device-id" &&
                r.DeviceName == "My TV" &&
                r.App == "Jellyfin" &&
                r.AppVersion == "2.0")),
            Times.Once);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Concrete ICryptoProvider for tests — avoids Moq's inability to handle ReadOnlySpan parameters.
    /// </summary>
    private sealed class FakeCryptoProvider : JellyfinCryptoProvider
    {
        public string DefaultHashMethod => "SHA256";

        public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password)
            => new PasswordHash("SHA256", Array.Empty<byte>());

        public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => true;

        public byte[] GenerateSalt() => Array.Empty<byte>();

        public byte[] GenerateSalt(int length) => new byte[length];
    }
}
