using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

/// <summary>
/// Kullanıcının kendi profili — me endpoint'leri (ADR-016).
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly TokenGenerator _tokens;
    public MeController(MimirDbContext db, TokenGenerator tokens) { _db = db; _tokens = tokens; }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id : throw new InvalidOperationException("user_id_missing");

    // GET /api/me — kendi profilim
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == CurrentUserId, ct);
        if (u is null) return Unauthorized();
        return Ok(new MeDto(
            u.Id, u.Username, u.Email, u.Phone,
            u.Status.ToString(), u.IsAdmin, u.FriendKey, u.CreatedAt));
    }

    // POST /api/me/regenerate-friend-key
    [HttpPost("regenerate-friend-key")]
    public async Task<IActionResult> RegenerateFriendKey(CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == CurrentUserId, ct);
        if (u is null) return Unauthorized();

        // Collision-free retry (12 char URL-safe ~10^21 entropy, retry rare)
        for (int i = 0; i < 5; i++)
        {
            var candidate = _tokens.GenerateUrlSafeToken(byteSize: 9); // ~12 char
            var taken = await _db.Users.AnyAsync(x => x.FriendKey == candidate, ct);
            if (!taken) { u.FriendKey = candidate; break; }
        }
        u.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new FriendKeyDto(u.FriendKey!));
    }
}
