using System.Net;
using System.Net.Http.Headers;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalRequestsAcceptNotModifiedOnlyWithSentValidators(bool useEtag)
    {
        var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var etag = useEtag ? EntityTagHeaderValue.Parse("\"one\"") : null;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            Assert.Equal(useEtag ? "\"one\"" : null, request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            Assert.Equal(useEtag ? null : (DateTimeOffset?)modified, request.Headers.IfModifiedSince);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }));

        var image = await AvatarDownload.LoadRemoteAsync(new Uri("https://images.example/avatar"), client, etag!, modified, CancellationToken.None);

        Assert.Null(image.Stream);
        Assert.Equal(etag, image.ETag);
        Assert.Equal(modified, image.LastModified);
    }

    [Fact]
    public async Task UnsolicitedNotModifiedIsRejected()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));

        await Assert.ThrowsAsync<HttpRequestException>(() => AvatarDownload.LoadRemoteAsync(
            new Uri("https://images.example/avatar"), client, null!, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/svg+xml", false)]
    public async Task ConditionalDownloadStillChecksContentTypeAndPreservesNoStore(string contentType, bool allowed)
    {
        using var client = new HttpClient(new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"two\"");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }));
        Task<AvatarImage> Load() => AvatarDownload.LoadRemoteAsync(
            new Uri("https://images.example/avatar"), client, EntityTagHeaderValue.Parse("\"one\""), null, CancellationToken.None);
        if (!allowed)
        {
            await Assert.ThrowsAsync<InvalidDataException>(Load);
            return;
        }

        var image = await Load();
        using var stream = image.Stream;
        Assert.True(image.NoStore);
        Assert.Equal("\"two\"", image.ETag.Tag);
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
