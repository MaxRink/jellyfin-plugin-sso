using System.Net.Http.Headers;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SSO_Auth.Api.Avatar;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace SSO_Auth.Tests;

public sealed class AvatarUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sso-avatar-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IProviderManager> _images = new();
    private readonly User _user = new("avatar-user", "SSO-Auth", "Default") { Id = Guid.NewGuid() };

    public AvatarUpdaterTests()
    {
        Directory.CreateDirectory(_root);
        var previous = Path.Combine(_root, "legacy.png");
        File.WriteAllBytes(previous, [1, 2, 3]);
        _user.ProfileImage = new ImageInfo(previous);
        _users.Setup(x => x.GetUserById(_user.Id)).Returns(_user);
        _users.Setup(x => x.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _images.Setup(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (Stream stream, string contentType, string path) =>
            {
                using var output = File.Create(path);
                await stream.CopyToAsync(output);
            });
    }

    [Fact]
    public async Task ReplacementIsSavedBeforePublishingAndKeepsLegacyFile()
    {
        var previous = CurrentImage;
        _images.Setup(x => x.SaveImage(It.IsAny<Stream>(), "image/png", It.IsAny<string>()))
            .Returns(async (Stream stream, string contentType, string path) =>
            {
                Assert.Same(previous, _user.ProfileImage);
                Assert.True(File.Exists(previous.Path));
                using var output = File.Create(path);
                await stream.CopyToAsync(output);
            });
        _users.Setup(x => x.UpdateUserAsync(_user)).Callback(() =>
        {
            Assert.True(File.Exists(CurrentImage.Path));
            Assert.True(File.Exists(previous.Path));
        }).Returns(Task.CompletedTask);
        var updater = Downloading([4, 5, 6]);

        await Refresh(updater);

        Assert.NotEqual(previous.Path, CurrentImage.Path);
        Assert.EndsWith(".png", CurrentImage.Path);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(CurrentImage.Path));
        Assert.True(File.Exists(previous.Path));
        _users.Verify(x => x.ClearProfileImageAsync(It.IsAny<User>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplacementPreservesOldImageAndRemovesPartialFile(bool failPersistence)
    {
        var previous = CurrentImage;
        if (failPersistence)
        {
            _users.Setup(x => x.UpdateUserAsync(_user)).ThrowsAsync(new IOException("write failed"));
        }
        else
        {
            _images.Setup(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((Stream stream, string contentType, string path) =>
                {
                    File.WriteAllBytes(path, [4]);
                    throw new IOException("image failed");
                });
        }

        await Assert.ThrowsAsync<IOException>(() => Refresh(Downloading([4, 5, 6])));

        Assert.Same(previous, _user.ProfileImage);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(previous.Path));
        Assert.Equal([previous.Path], Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnchangedBodyUpdatesValidatorsWithoutWritingAgain()
    {
        var calls = 0;
        var updater = new AvatarUpdater((url, etag, modified) =>
        {
            Assert.Equal(calls == 0 ? null : calls == 1 ? "\"one\"" : "\"two\"", etag?.Tag);
            calls++;
            return Task.FromResult(Image([4, 5, 6], calls == 1 ? "\"one\"" : "\"two\""));
        });
        await Refresh(updater);
        var path = CurrentImage.Path;

        await Refresh(updater);
        await Refresh(updater);

        Assert.Equal(path, CurrentImage.Path);
        _images.Verify(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _users.Verify(x => x.UpdateUserAsync(It.IsAny<User>()), Times.Once);
    }

    [Fact]
    public async Task NotModifiedKeepsExistingFileAndDoesNotPersistUser()
    {
        var calls = 0;
        var updater = new AvatarUpdater((url, etag, modified) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Image([4], "\"one\"") : new AvatarImage(null!, null!, etag, modified));
        });
        await Refresh(updater);
        var path = CurrentImage.Path;

        await Refresh(updater);

        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(path));
        _images.Verify(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _users.Verify(x => x.UpdateUserAsync(It.IsAny<User>()), Times.Once);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("removed-file")]
    [InlineData("different-profile")]
    [InlineData("no-store")]
    public async Task ChangedResourcesDoNotReuseValidators(string change)
    {
        var calls = 0;
        var updater = new AvatarUpdater((url, etag, modified) =>
        {
            Assert.Null(etag);
            Assert.Null(modified);
            calls++;
            return Task.FromResult(Image([4], "\"one\"") with { NoStore = change == "no-store" });
        });
        await Refresh(updater);
        if (change == "removed-file") File.Delete(CurrentImage.Path);
        if (change == "different-profile") _user.ProfileImage = new ImageInfo(Path.Combine(_root, "manual.png"));

        await Refresh(updater, change == "url" ? "https://other.example/avatar" : "https://images.example/avatar");

        Assert.Equal(2, calls);
        _images.Verify(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ConcurrentRefreshesForOneUserSerializeAndRemoveOnlyObsoleteOwnedFile()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var updater = new AvatarUpdater(async (url, etag, modified) =>
        {
            if (++calls == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }

            return Image([(byte)calls], "\"" + calls + "\"");
        });
        var first = Refresh(updater);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Refresh(updater);
        Assert.Equal(1, calls);
        releaseFirst.SetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(CurrentImage.Path));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, _user.Id.ToString("N"))));
        Assert.True(File.Exists(Path.Combine(_root, "legacy.png")));
    }

    [Fact]
    public async Task ProfileEditedWhileSavingIsNotOverwritten()
    {
        var manual = new ImageInfo(Path.Combine(_root, "manual.png"));
        _images.Setup(x => x.SaveImage(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((Stream stream, string type, string path) =>
            {
                File.WriteAllBytes(path, [4]);
                _user.ProfileImage = manual;
                return Task.CompletedTask;
            });

        await Refresh(Downloading([4]));

        Assert.Same(manual, _user.ProfileImage);
        _users.Verify(x => x.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, _user.Id.ToString("N"))));
    }

    [Fact]
    public async Task CacheEvictsOldEntriesInsteadOfGrowingWithUserCount()
    {
        var accounts = new Dictionary<Guid, User>();
        _users.Setup(x => x.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => accounts[id]);
        var validators = new List<string?>();
        var updater = new AvatarUpdater((url, etag, modified) =>
        {
            validators.Add(etag?.Tag);
            return Task.FromResult(Image([4], "\"one\""));
        });
        for (var index = 0; index < 1025; index++)
        {
            var account = new User("cache-" + index, "SSO-Auth", "Default") { Id = Guid.NewGuid() };
            accounts.Add(account.Id, account);
            await updater.RefreshAsync(account.Id, "https://images.example/avatar", _users.Object, _images.Object, _root, NullLogger.Instance);
        }

        validators.Clear();
        foreach (var account in accounts.Values)
        {
            await updater.RefreshAsync(account.Id, "https://images.example/avatar", _users.Object, _images.Object, _root, NullLogger.Instance);
        }

        Assert.Contains(validators, tag => tag is null);
    }

    private ImageInfo CurrentImage => Assert.IsType<ImageInfo>(_user.ProfileImage);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static AvatarImage Image(byte[] bytes, string? etag = null) =>
        new(new MemoryStream(bytes), "image/png", etag is null ? null : EntityTagHeaderValue.Parse(etag));

    private static AvatarUpdater Downloading(byte[] bytes) => new((url, etag, modified) => Task.FromResult(Image(bytes)));

    private Task Refresh(AvatarUpdater updater, string url = "https://images.example/avatar") =>
        updater.RefreshAsync(_user.Id, url, _users.Object, _images.Object, _root, NullLogger.Instance);
}
