using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetVersion;

public class GetVersionController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public const string Version = "4.5.1";
    protected override bool RequiresAuthentication => false;

    protected override Task<IActionResult> Handle()
    {
        // Read version and build timestamp from environment variables, with fallbacks
        var version = Environment.GetEnvironmentVariable("NZBDAV_VERSION") ?? Version;
        var buildTimestamp = Environment.GetEnvironmentVariable("NZBDAV_BUILD_TIMESTAMP");

        var response = new GetVersionResponse()
        {
            Version = version,
            BuildTimestamp = buildTimestamp
        };
        return Task.FromResult<IActionResult>(Ok(response));
    }
}