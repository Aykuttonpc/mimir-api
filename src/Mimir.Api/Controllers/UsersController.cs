using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;

namespace Mimir.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly MimirDbContext _db;

    public UsersController(MimirDbContext db) => _db = db;

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("user_id_missing");

    /// <summary>
    /// DM için seçilebilir kullanıcılar — Active olan, current user dışındaki herkes (ADR-013).
    /// Username/email substring search ile filtre.
    /// </summary>
    [HttpGet("active")]
    public async Task<IActionResult> ActiveUsers(
        [FromQuery] string? search,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var me = CurrentUserId;
        var q = _db.Users
            .AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && u.Id != me);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(u =>
                EF.Functions.Like(u.Username.ToLower(), $"%{s}%") ||
                EF.Functions.Like(u.Email.ToLower(), $"%{s}%"));
        }

        var users = await q
            .OrderBy(u => u.Username)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(u => new ActiveUserDto(u.Id, u.Username))
            .ToListAsync(ct);

        return Ok(users);
    }
}
