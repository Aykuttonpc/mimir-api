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
    /// ADR-016: tüm Active kullanıcı listesi PRIVACY ihlali — kaldırıldı.
    /// Yeni model: kullanıcılar friend key paylaşımı + onay-bazlı eklenir (FriendsController).
    /// </summary>
    [HttpGet("active")]
    public IActionResult ActiveUsers() =>
        StatusCode(StatusCodes.Status410Gone, new
        {
            error = "endpoint_removed_adr_016",
            message = "Tüm aktif kullanıcılar listesi privacy gereği kaldırıldı. Friend key ile arkadaş ekle: POST /api/friends/requests"
        });
}
