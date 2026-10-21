using System;
using System.IO;
using System.Net.Http.Headers;

namespace Jellyfin.Plugin.SSO_Auth.Api.Avatar;

internal sealed record AvatarImage(
    MemoryStream Stream,
    string ContentType,
    EntityTagHeaderValue ETag = null,
    DateTimeOffset? LastModified = null,
    bool NoStore = false);
