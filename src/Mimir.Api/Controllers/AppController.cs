using Microsoft.AspNetCore.Mvc;
using Mimir.Api.Contracts;

namespace Mimir.Api.Controllers;

/// <summary>
/// Public endpoint'ler — auth gerekmez.
/// T-039: app version info (force-update).
/// </summary>
[ApiController]
[Route("api/app")]
public class AppController : ControllerBase
{
    private readonly IConfiguration _config;
    public AppController(IConfiguration config) => _config = config;

    // GET /api/app/version?platform=android (default android)
    [HttpGet("version")]
    public IActionResult GetVersion([FromQuery] string platform = "android")
    {
        var key = platform.Equals("ios", StringComparison.OrdinalIgnoreCase) ? "Ios" : "Android";
        return Ok(new AppVersionInfoDto(
            MinSupportedVersion: _config[$"MinAppVersion:{key}"] ?? "0.0.0",
            LatestVersion: _config[$"LatestAppVersion:{key}"] ?? "0.0.0",
            DownloadUrl: _config[$"AppDownloadUrl:{key}"] ?? "",
            Platform: platform.ToLowerInvariant()
        ));
    }
}
