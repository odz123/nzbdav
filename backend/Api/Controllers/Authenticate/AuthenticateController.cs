using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Authenticate;

[ApiController]
[Route("api/authenticate")]
public class AuthenticateController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<AuthenticateResponse> Authenticate(AuthenticateRequest request)
    {
        var account = await dbClient.Ctx.Accounts
            .Where(a => a.Type == request.Type && a.Username == request.Username)
            .FirstOrDefaultAsync();

        // Always verify password to prevent timing attacks that could enumerate valid usernames
        // Use dummy values if account doesn't exist to maintain consistent timing
        var passwordHash = account?.PasswordHash ?? "AQAAAAIAAYagAAAAEDummyHashForTimingProtection1234567890abcdefghijklmnopqrstuvwxyz";
        var salt = account?.RandomSalt ?? "";
        var passwordValid = PasswordUtil.Verify(passwordHash, request.Password, salt);

        return new AuthenticateResponse()
        {
            Authenticated = account != null && passwordValid
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new AuthenticateRequest(HttpContext);
        var response = await Authenticate(request);
        return Ok(response);
    }
}