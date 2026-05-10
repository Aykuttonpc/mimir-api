using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mimir.Api.Contracts;

namespace Mimir.Api.Controllers;

/// <summary>
/// Sprint #12 — WebRTC voice call.
/// TURN credentials HMAC-time-limited (coturn `use-auth-secret` ile uyumlu).
/// Kullanıcı /api/call/turn-credentials çağırır → 1 saatlik TURN urls + creds döner.
/// Her arama öncesi yeni token (replay-attack koruması).
/// </summary>
[ApiController]
[Route("api/call")]
[Authorize]
public class CallController : ControllerBase
{
    private readonly IConfiguration _config;
    public CallController(IConfiguration config) { _config = config; }

    [HttpGet("turn-credentials")]
    public IActionResult GetTurnCredentials()
    {
        var secret = _config["Turn:Secret"];
        var realm = _config["Turn:Realm"] ?? "mimir";
        var host = _config["Turn:Host"] ?? "";

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(host))
        {
            // TURN yapılandırılmadıysa boş döner — mobile P2P-only mod'a düşer (NAT bypass başarısız olursa arama kurulamaz).
            return Ok(new TurnCredentialsDto(
                Urls: new List<string>(),
                Username: "",
                Credential: "",
                ExpiresAt: 0
            ));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "anon";

        // coturn TURN REST API: username = "<unix-expiry-ts>:<userId>", password = base64(HMAC-SHA1(secret, username))
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var username = $"{expiresAt}:{userId}";

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        var credential = Convert.ToBase64String(hash);

        return Ok(new TurnCredentialsDto(
            Urls: new List<string>
            {
                $"stun:{host}:3478",
                $"turn:{host}:3478?transport=udp",
                $"turn:{host}:3478?transport=tcp",
                $"turns:{host}:5349?transport=tcp",
            },
            Username: username,
            Credential: credential,
            ExpiresAt: expiresAt
        ));
    }
}
