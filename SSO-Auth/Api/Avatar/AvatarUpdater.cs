// SPDX-FileCopyrightText: NekoShinobi and the jellyfin-plugin-sso authors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Api.Avatar;

/// <summary>Publishes avatar replacements only after their file has been saved.</summary>
internal sealed class AvatarUpdater
{
    private const int MaximumCachedUsers = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CachedImage> _cache = new();
    private readonly Dictionary<Guid, RefreshLock> _refreshes = new();
    private readonly Func<string, EntityTagHeaderValue, DateTimeOffset?, Task<AvatarImage>> _download;

    internal AvatarUpdater()
        : this(AvatarDownload.LoadAsync)
    {
    }

    internal AvatarUpdater(Func<string, EntityTagHeaderValue, DateTimeOffset?, Task<AvatarImage>> download)
    {
        _download = download;
    }

    internal async Task RefreshAsync(Guid userId, string url, IUserManager users, IProviderManager images, string root, ILogger logger)
    {
        RefreshLock entry;
        lock (_gate)
        {
            if (!_refreshes.TryGetValue(userId, out entry))
            {
                entry = new RefreshLock();
                _refreshes.Add(userId, entry);
            }

            entry.Users++;
        }

        await entry.Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(userId, url, users, images, root, logger).ConfigureAwait(false);
        }
        finally
        {
            entry.Semaphore.Release();
            lock (_gate)
            {
                if (--entry.Users == 0)
                {
                    _refreshes.Remove(userId);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task RefreshCoreAsync(Guid userId, string url, IUserManager users, IProviderManager images, string root, ILogger logger)
    {
        var user = users.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var previousPath = user.ProfileImage?.Path;
        var urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        CachedImage cached;
        lock (_gate)
        {
            _cache.TryGetValue(userId, out cached);
        }

        if (cached?.UrlHash != urlHash || cached.Path != previousPath || !File.Exists(cached.Path))
        {
            cached = null;
        }

        var avatar = await _download(url, cached?.ETag, cached?.LastModified).ConfigureAwait(false);
        using var stream = avatar.Stream;
        user = users.GetUserById(userId);
        if (user is null || user.ProfileImage?.Path != previousPath)
        {
            Forget(userId);
            return;
        }

        if (stream is null)
        {
            if (cached is not null && File.Exists(cached.Path))
            {
                Remember(userId, cached with { ETag = avatar.ETag, LastModified = avatar.LastModified }, avatar.NoStore);
            }
            else
            {
                Forget(userId);
            }

            return;
        }

        var digest = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        if (cached?.Digest == digest && File.Exists(cached.Path))
        {
            Remember(userId, cached with { ETag = avatar.ETag, LastModified = avatar.LastModified }, avatar.NoStore);
            return;
        }

        AvatarContentType.TryResolveExtension(avatar.ContentType, out var extension);
        var directory = Path.Combine(root, userId.ToString("N"));
        if (new DirectoryInfo(directory).LinkTarget is not null)
        {
            throw new InvalidDataException("Avatar directory is a symbolic link.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile-sso-" + Guid.NewGuid().ToString("N") + extension);
        var published = false;
        try
        {
            await images.SaveImage(stream, avatar.ContentType, path).ConfigureAwait(false);
            user = users.GetUserById(userId);
            if (user is null || user.ProfileImage?.Path != previousPath)
            {
                Forget(userId);
                return;
            }

            var previousImage = user.ProfileImage;
            user.ProfileImage = new ImageInfo(path);
            try
            {
                await users.UpdateUserAsync(user).ConfigureAwait(false);
                published = true;
            }
            catch
            {
                user.ProfileImage = previousImage;
                throw;
            }

            Remember(userId, new CachedImage(urlHash, path, digest, avatar.ETag, avatar.LastModified), avatar.NoStore);
            // Only remove files this updater owns, after the new reference has been persisted.
            if (previousPath is not null && Path.GetDirectoryName(previousPath) == directory
                && Path.GetFileName(previousPath).StartsWith("profile-sso-", StringComparison.Ordinal))
            {
                DeleteUnusedImage(previousPath, logger);
            }
        }
        finally
        {
            if (!published)
            {
                DeleteUnusedImage(path, logger);
            }
        }
    }

    private static void DeleteUnusedImage(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            logger.LogWarning("Could not remove an unused SSO avatar.");
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove an unused SSO avatar.");
        }
    }

    private void Remember(Guid userId, CachedImage image, bool noStore)
    {
        lock (_gate)
        {
            _cache.Remove(userId);
            if (noStore)
            {
                return;
            }

            if (_cache.Count >= MaximumCachedUsers)
            {
                _cache.Remove(_cache.Keys.First());
            }

            _cache.Add(userId, image);
        }
    }

    private void Forget(Guid userId)
    {
        lock (_gate)
        {
            _cache.Remove(userId);
        }
    }

    private sealed record CachedImage(string UrlHash, string Path, string Digest, EntityTagHeaderValue ETag, DateTimeOffset? LastModified);

    private sealed class RefreshLock
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int Users { get; set; }
    }
}
