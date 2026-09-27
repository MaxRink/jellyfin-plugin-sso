using Jellyfin.Plugin.SSO_Auth.Auth;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace SSO_Auth.Tests;

public class OidcClientSecretTests
{
    [Fact]
    public void LegacyAndPublicClientsKeepTheirBehavior()
    {
        Assert.Null(OidcClientSecret.Resolve(new OidConfig()));
        Assert.Equal("secret", OidcClientSecret.Resolve(new OidConfig { OidSecret = " secret " }));
    }

    [Fact]
    public void FileReferenceReadsRotatedSecretAndDoesNotFallBack()
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = new OidConfig { OidSecretFile = path, OidSecret = "stale" };
            File.WriteAllText(path, "first\n");
            Assert.Equal("first", OidcClientSecret.Resolve(config));
            File.WriteAllText(path, "second\n");
            Assert.Equal("second", OidcClientSecret.Resolve(config));
            File.WriteAllText(path, "");
            Assert.Throws<InvalidOperationException>(() => OidcClientSecret.Resolve(config));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingEnvironmentReferenceDoesNotFallBack()
    {
        Assert.Throws<InvalidOperationException>(() => OidcClientSecret.Resolve(new OidConfig
        { OidSecret = "stale", OidSecretEnvironmentVariable = "JELLYFIN_TEST_MISSING_" + Guid.NewGuid().ToString("N") }));
    }
}
