using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.SSO_Auth.Auth;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.EventConsumers;

/// <summary>
/// Applies the SSO-only login setting to users created while it is switched on.
/// </summary>
public class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IUserManager _userManager;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public UserCreatedConsumer(IUserManager userManager, ILogger<UserCreatedConsumer> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        if (eventArgs?.Argument is not null)
        {
            await SsoOnlyEnforcer.EnforceAsync(_userManager, _logger, eventArgs.Argument).ConfigureAwait(false);
        }
    }
}
