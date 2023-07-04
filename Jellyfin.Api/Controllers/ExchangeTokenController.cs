// using MediaBrowser.Model.Users;
// using MediaBrowser.Controller.Authentication;
// using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Exchange Token Controller.
/// </summary>
[Route("ExchangeTokens")]
public class ExchangeTokenController : BaseJellyfinApiController
{
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExchangeTokenController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    public ExchangeTokenController(
        IUserManager userManager,
        IAuthorizationContext authContext)
    {
        _userManager = userManager;
        _authContext = authContext;
    }
}
