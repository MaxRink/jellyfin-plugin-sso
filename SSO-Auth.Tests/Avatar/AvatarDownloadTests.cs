using Jellyfin.Plugin.SSO_Auth.Api.Avatar;

namespace SSO_Auth.Tests;

public class AvatarDownloadTests
{
    [Fact]
    public async Task InlineRasterIsDecoded()
    {
        var avatar = await AvatarDownload.LoadAsync("data:image/png;base64,AQID");
        using var stream = avatar.Stream;
        Assert.Equal("image/png", avatar.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,AQID")]
    [InlineData("data:text/html;base64,AQID")]
    [InlineData("data:image/png,hello")]
    [InlineData("http://127.0.0.1/avatar.png")]
    [InlineData("http://169.254.169.254/avatar.png")]
    [InlineData("file:///etc/passwd")]
    public async Task UnsafeAvatarIsRejected(string url)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => AvatarDownload.LoadAsync(url));
    }

    [Fact]
    public async Task OversizedInlineAvatarIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => AvatarDownload.LoadAsync(
            "data:image/png;base64," + new string('A', AvatarDownload.MaximumBytes * 2)));
    }
}
