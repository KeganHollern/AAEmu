using System.Net;
using System.Text.RegularExpressions;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Services.WebApi.Models;
using NetCoreServer;

namespace AAEmu.Game.Services.WebApi.Controllers;

internal class CommandController : BaseController
{
    [WebApiPost("/api/commands/([^/]+)")]
    public HttpResponse ExecuteCommand(HttpRequest request, MatchCollection matches, WebApiRequestContext context)
    {
        // A character name in an unauthenticated request is not a staff identity.
        CommandManager.Instance.RejectWebApiCommand(context.RemoteAddress, matches[0].Groups[1].Value, request.Body);
        return JsonResponse(HttpStatusCode.Forbidden,
            new ErrorModel("Use an authenticated game session for staff commands."));
    }
}
