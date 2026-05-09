using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;

namespace Mimir.Api.Controllers;

// ADR-017: FCM push token kaydı. Login sonrası mobile token'ını gönderir,
// logout / hesap değişiminde siler. Aynı kullanıcı çoklu cihaz desteklenir.
[ApiController]
[Route("api/me/devices")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly MimirDbContext _db;
    public DevicesController(MimirDbContext db) { _db = db; }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id : throw new InvalidOperationException("user_id_missing");

    // POST /api/me/devices — token kaydet veya refresh
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<DevicePlatform>(req.Platform, ignoreCase: true, out var platform))
            return BadRequest(new { error = "invalid_platform" });

        var existing = await _db.UserDeviceTokens
            .FirstOrDefaultAsync(t => t.FcmToken == req.FcmToken, ct);

        if (existing is not null)
        {
            // Token aynı kullanıcıya yeniden kaydedilirse: LastSeenAt güncelle.
            // Token başka kullanıcıdan bu kullanıcıya devrolduysa (logout/login farklı user):
            // sahibi değiştir, eski user kaybeder. (Aynı cihazda farklı user.)
            existing.UserId = CurrentUserId;
            existing.Platform = platform;
            existing.LastSeenAt = DateTime.UtcNow;
        }
        else
        {
            _db.UserDeviceTokens.Add(new UserDeviceToken
            {
                UserId = CurrentUserId,
                FcmToken = req.FcmToken,
                Platform = platform,
            });
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // DELETE /api/me/devices/{token} — logout veya app uninstall öncesi
    [HttpDelete("{token}")]
    public async Task<IActionResult> Unregister(string token, CancellationToken ct)
    {
        var me = CurrentUserId;
        await _db.UserDeviceTokens
            .Where(t => t.FcmToken == token && t.UserId == me)
            .ExecuteDeleteAsync(ct);
        return NoContent();
    }
}
