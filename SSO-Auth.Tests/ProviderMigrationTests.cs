using System.Diagnostics;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Api.Migration;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Moq;
using Newtonsoft.Json;

namespace SSO_Auth.Tests;

public class ProviderMigrationTests
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly Guid _folder = Guid.NewGuid();
    private readonly PluginConfiguration _config = new();

    public ProviderMigrationTests()
    {
        _config.OidConfigs["idp"] = new OidConfig { OidEndpoint = "https://id.example" };
        _config.OidConfigs["idp"].CanonicalLinks["legacy-name"] = _id;
    }

    private UserDto? User(Guid id) => id == _id ? new UserDto
    { Id = id, Name = "legacy-name", Policy = new UserPolicy { EnabledFolders = new[] { _folder }, EnableAllFolders = false } } : null;

    private MigrationPreview Plan()
    {
        var plan = ProviderModelMigration.Preview(_config, "oid", "idp", User);
        plan.Links[0].Subject = "confirmed-subject";
        plan.ManualFolders[_id.ToString("D")] = new ManualFolderGrant { Folders = new[] { _folder.ToString("D") } };
        return plan;
    }

    [Fact]
    public void PreviewShowsCurrentAccessWithoutGuessingSubjectsOrManualGrants()
    {
        var preview = ProviderModelMigration.Preview(_config, "oid", "idp", User);
        Assert.Null(preview.Links[0].Subject);
        Assert.Empty(preview.ManualFolders);
        Assert.Equal(_folder.ToString("D"), Assert.Single(preview.CurrentFolders[_id.ToString("D")].Folders));
        Assert.False(_config.OidConfigs["idp"].Migration.Enabled);
    }

    [Fact]
    public void ApplyPreservesUserIdsAndOriginalConfigAndSurvivesXmlRoundtrip()
    {
        var result = ProviderModelMigration.Apply(_config, "oid", "idp", Plan(), User);
        Assert.Equal(_id, result.OidConfigs["idp"].CanonicalLinks["confirmed-subject"]);
        Assert.Equal(_id, _config.OidConfigs["idp"].CanonicalLinks["legacy-name"]);
        Assert.False(_config.OidConfigs["idp"].Migration.Enabled);
        using var output = new MemoryStream();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.Serialize(output, result);
        output.Position = 0;
        var restored = (PluginConfiguration)serializer.Deserialize(output)!;
        Assert.True(restored.OidConfigs["idp"].Migration.Enabled);
        Assert.Equal(_folder.ToString("D"), Assert.Single(restored.OidConfigs["idp"].Migration.ManualFolders[_id.ToString("D")].Folders));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("subject")]
    [InlineData("user")]
    [InlineData("issuer")]
    [InlineData("manual")]
    [InlineData("folder")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("duplicate")]
    public void InvalidPlansNeverMutateTheLiveConfig(string error)
    {
        var plan = Plan();
        switch (error)
        {
            case "stale": plan.Fingerprint = "old"; break;
            case "subject": plan.Links[0].Subject = " "; break;
            case "user": plan.Links[0].UserId = Guid.NewGuid(); break;
            case "issuer": plan.Issuer = "https://other.example"; break;
            case "manual": plan.ManualFolders.Clear(); break;
            case "folder": plan.ManualFolders[_id.ToString("D")].Folders = new[] { "not-an-id" }; break;
            case "missing": plan.Links = Array.Empty<MigrationLink>(); break;
            case "null": plan.Links = new MigrationLink[] { null! }; break;
            case "duplicate": plan.Links = new[] { plan.Links[0], plan.Links[0] }; break;
        }
        var before = JsonConvert.SerializeObject(_config);
        Assert.Throws<InvalidOperationException>(() => ProviderModelMigration.Apply(_config, "oid", "idp", plan, User));
        Assert.Equal(before, JsonConvert.SerializeObject(_config));
    }

    [Fact]
    public void MissingAccountsAndAmbiguousSubjectsAreRejected()
    {
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() => ProviderModelMigration.Apply(_config, "oid", "idp", plan, _ => null));
        _config.OidConfigs["idp"].CanonicalLinks["other-key"] = _id;
        plan = Plan();
        plan.Links[1].Subject = plan.Links[0].Subject;
        Assert.Throws<InvalidOperationException>(() => ProviderModelMigration.Apply(_config, "oid", "idp", plan, User));
    }

    [Fact]
    public void SamlUsesTheSameExplicitMigrationAndPinsItsAuthority()
    {
        _config.SamlConfigs["saml"] = new SamlConfig { SamlEndpoint = "https://id.example/saml" };
        _config.SamlConfigs["saml"].CanonicalLinks["old"] = _id;
        var plan = ProviderModelMigration.Preview(_config, "saml", "saml", User);
        plan.Links[0].Subject = "confirmed-nameid";
        plan.ManualFolders[_id.ToString("D")] = new ManualFolderGrant();
        var result = ProviderModelMigration.Apply(_config, "saml", "saml", plan, User);
        var provider = result.SamlConfigs["saml"];
        ProviderModelMigration.EnsureAuthority("saml", provider, provider.Migration);
        provider.SamlEndpoint = "https://replacement.example";
        Assert.Throws<InvalidOperationException>(() => ProviderModelMigration.EnsureAuthority("saml", provider, provider.Migration));
    }

    [Fact]
    public async Task OfflineToolPreviewsAppliesAndRestoresExactBackupAndRejectsChangedConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sso-migration-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var configPath = Path.Combine(directory, "plugin.xml");
            using (var stream = File.Create(configPath)) new XmlSerializer(typeof(PluginConfiguration)).Serialize(stream, _config);
            var original = File.ReadAllBytes(configPath);
            var users = Path.Combine(directory, "users.json");
            File.WriteAllText(users, JsonConvert.SerializeObject(new[] { User(_id) }));
            var planPath = Path.Combine(directory, "plan.json");
            Assert.Equal(0, await RunTool("preview", configPath, users, "oid", "idp", planPath));
            Assert.Equal(1, await RunTool("apply", configPath, users, "oid", "idp", planPath, "--server-stopped"));
            Assert.Equal(original, File.ReadAllBytes(configPath));
            var reviewed = JsonConvert.DeserializeObject<MigrationPreview>(File.ReadAllText(planPath))!;
            reviewed.Links[0].Subject = "confirmed-subject";
            reviewed.ManualFolders[_id.ToString("D")] = new ManualFolderGrant();
            File.WriteAllText(planPath, JsonConvert.SerializeObject(reviewed));
            Assert.Equal(0, await RunTool("apply", configPath, users, "oid", "idp", planPath, "--server-stopped"));
            var migrated = File.ReadAllBytes(configPath);
            Assert.NotEqual(original, migrated);
            var backup = Assert.Single(Directory.GetFiles(directory, "*.before-migration"));
            File.AppendAllText(configPath, "\n");
            Assert.Equal(1, await RunTool("rollback", configPath, backup, backup + ".receipt.json", "--server-stopped"));
            File.WriteAllBytes(configPath, migrated);
            Assert.Equal(0, await RunTool("rollback", configPath, backup, backup + ".receipt.json", "--server-stopped"));
            Assert.Equal(original, File.ReadAllBytes(configPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<int> RunTool(params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SSO-Auth.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools", "SSO-Migration", "bin", configuration, "net10.0", "SSO-Migration.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}

public partial class OidDeviceAuthTests
{
    [Fact]
    public async Task MigratedIdentityCannotAdoptAnAccountThroughALegacyUsernameKey()
    {
        SetConfig(new OidConfig { OidEndpoint = Issuer });
        var config = SSOPlugin.Instance.Configuration.OidConfigs[Provider];
        config.Migration.Enabled = true;
        config.Migration.Issuer = config.OidEndpoint.Trim();
        config.CanonicalLinks["testuser"] = _testUserId;
        _userManager.Setup(m => m.GetUserByName("testuser")).Returns(_testUser);
        var method = typeof(SSOController).GetMethod("CreateCanonicalLinkAndUserIfNotExist", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<Guid?>)method.Invoke(_controller, new object[] { "oid", Provider, "new-subject", "testuser" })!;
        Assert.Null(result);
        Assert.False(config.CanonicalLinks.ContainsKey("new-subject"));
    }

    [Fact]
    public async Task ExplicitManualGrantsSurviveWhileRemovedRolesLoseAccess()
    {
        var manual = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var role = Guid.NewGuid();
        var policy = new UserPolicy { EnabledFolders = new[] { revoked }, EnableAllFolders = false };
        _testUser.Password = "existing";
        _userManager.Setup(m => m.GetUserDto(_testUser, It.IsAny<string>())).Returns(new UserDto { Policy = policy });
        SetConfig(new OidConfig { OidEndpoint = Issuer });
        var migration = SSOPlugin.Instance.Configuration.OidConfigs[Provider].Migration;
        migration.Enabled = true;
        migration.ManualFolders[_testUserId.ToString("D")] = new ManualFolderGrant { Folders = new[] { manual.ToString() } };
        var method = typeof(SSOController).GetMethod("Authenticate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_controller, new object?[] { _testUserId, false, true, false,
            new[] { role.ToString() }, false, false, new AuthResponse(), null, null, false, null, true,
            Array.Empty<string>(), migration })!;
        Assert.Equal(new[] { manual, role }.Order(), policy.EnabledFolders.Order());
        Assert.True(migration.PolicyApplied);
        _userManager.Verify(m => m.UpdatePolicyAsync(_testUserId, policy), Times.Once);
    }
}
