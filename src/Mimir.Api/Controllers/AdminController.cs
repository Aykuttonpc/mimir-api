using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "Admin")]
public class AdminController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly TokenGenerator _tokens;

    public AdminController(MimirDbContext db, TokenGenerator tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("user_id_missing");

    // GET /api/admin/invitations — T-040: davet listesi (token plain dönmez)
    [HttpGet("invitations")]
    public async Task<IActionResult> ListInvitations(CancellationToken ct)
    {
        var invs = await _db.Invitations
            .AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var redeemerIds = invs
            .Where(i => i.RedeemedByUserId != null)
            .Select(i => i.RedeemedByUserId!.Value)
            .Distinct()
            .ToList();

        var redeemers = await _db.Users
            .AsNoTracking()
            .Where(u => redeemerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var now = DateTime.UtcNow;
        var dtos = invs.Select(i => new InvitationSummaryDto(
            Id: i.Id,
            Note: i.Note,
            CreatedAt: i.CreatedAt,
            ExpiresAt: i.ExpiresAt,
            RedeemedAt: i.RedeemedAt,
            RedeemedByUsername: i.RedeemedByUserId.HasValue
                ? redeemers.GetValueOrDefault(i.RedeemedByUserId.Value)
                : null,
            Status: i.RedeemedAt.HasValue ? "Used"
                : i.ExpiresAt <= now ? "Expired"
                : "Active"
        )).ToList();

        return Ok(dtos);
    }

    // DELETE /api/admin/invitations/{id} — T-040: revoke (ExpiresAt'i şimdiye çek)
    [HttpDelete("invitations/{id:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken ct)
    {
        var inv = await _db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inv is null) return NotFound();
        if (inv.RedeemedAt.HasValue) return BadRequest(new { error = "already_redeemed" });
        if (inv.ExpiresAt <= DateTime.UtcNow) return BadRequest(new { error = "already_expired" });

        inv.ExpiresAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // POST /api/admin/invitations
    [HttpPost("invitations")]
    [EnableRateLimiting("admin-invite")]
    public async Task<IActionResult> CreateInvitation([FromBody] InvitationCreateRequest req, CancellationToken ct)
    {
        var token = _tokens.GenerateUrlSafeToken(32);
        var expires = DateTime.UtcNow.AddDays(req.ExpiryDays ?? 7);
        var invitation = new Invitation
        {
            TokenHash = _tokens.Sha256Hex(token),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            IssuedByUserId = CurrentUserId,
            ExpiresAt = expires,
        };
        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync(ct);

        return Ok(new InvitationCreateResponse(invitation.Id, token, expires));
    }

    // GET /api/admin/users/pending
    [HttpGet("users/pending")]
    public async Task<IActionResult> ListPending(CancellationToken ct)
    {
        var users = await _db.Users
            .Where(u => u.Status == UserStatus.PendingAdmin)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new PendingUserDto(u.Id, u.Username, u.Email, u.Phone, u.CreatedAt))
            .ToListAsync(ct);
        return Ok(users);
    }

    // POST /api/admin/users/{id}/approve
    [HttpPost("users/{id:guid}/approve")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound();

        if (user.Status != UserStatus.PendingAdmin && req.Decision != "suspend")
            return BadRequest(new { error = "user_not_pending" });

        var decision = req.Decision switch
        {
            "approve" => ApprovalDecision.Approved,
            "reject"  => ApprovalDecision.Rejected,
            "suspend" => ApprovalDecision.Suspended,
            _ => throw new ArgumentException("invalid_decision"),
        };

        user.Status = decision switch
        {
            ApprovalDecision.Approved  => UserStatus.Active,
            ApprovalDecision.Rejected  => UserStatus.Suspended,
            ApprovalDecision.Suspended => UserStatus.Suspended,
            _ => user.Status,
        };
        user.UpdatedAt = DateTime.UtcNow;

        _db.AdminApprovals.Add(new AdminApproval
        {
            UserId = user.Id,
            ApprovedByUserId = CurrentUserId,
            Decision = decision,
            Reason = req.Reason,
        });

        // Reject/Suspend → tüm refresh token'ları revoke et
        if (decision != ApprovalDecision.Approved)
        {
            var active = await _db.RefreshTokens
                .Where(r => r.UserId == user.Id && r.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var t in active)
            {
                t.RevokedAt = DateTime.UtcNow;
                t.RevokedReason = "admin_revoke";
            }
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
