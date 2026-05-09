using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Services.Email;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IEmailSender _email;
    private readonly TokenGenerator _tokens;
    private readonly ILogger<AuthController> _logger;
    private readonly IConfiguration _config;

    public AuthController(
        MimirDbContext db,
        IPasswordHasher hasher,
        IJwtService jwt,
        IEmailSender email,
        TokenGenerator tokens,
        ILogger<AuthController> logger,
        IConfiguration config)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _email = email;
        _tokens = tokens;
        _logger = logger;
        _config = config;
    }

    // POST /api/auth/register
    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        var inviteHash = _tokens.Sha256Hex(req.InvitationToken);
        var invitation = await _db.Invitations
            .FirstOrDefaultAsync(i => i.TokenHash == inviteHash && i.RedeemedByUserId == null && i.ExpiresAt > DateTime.UtcNow, ct);

        if (invitation is null)
            return BadRequest(new { error = "invalid_or_expired_invitation" });

        // Uniqueness — generic error to avoid email enumeration
        var emailNormalized = req.Email.Trim().ToLowerInvariant();
        var usernameNormalized = req.Username.Trim();
        var conflict = await _db.Users.AnyAsync(
            u => u.Email == emailNormalized || u.Username == usernameNormalized, ct);
        if (conflict)
            return BadRequest(new { error = "registration_failed" });

        var user = new User
        {
            Username = usernameNormalized,
            Email = emailNormalized,
            Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
            PasswordHash = _hasher.Hash(req.Password),
            Status = UserStatus.PendingEmail,
            IsAdmin = false,
        };
        _db.Users.Add(user);

        invitation.RedeemedByUserId = user.Id;
        invitation.RedeemedAt = DateTime.UtcNow;

        // Email verification token (30 dk)
        var emailToken = _tokens.GenerateUrlSafeToken(32);
        _db.OtpCodes.Add(new OtpCode
        {
            UserId = user.Id,
            CodeHash = _tokens.Sha256Hex(emailToken),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });

        await _db.SaveChangesAsync(ct);

        var verifyUrl = $"https://aykutonpc.com/mimir/api/auth/verify-email?token={emailToken}";
        await _email.SendAsync(user.Email,
            "Mimir — E-posta doğrulama",
            $"Merhaba {user.Username},\n\nE-posta adresini doğrulamak için: {verifyUrl}\n\nBağlantı 30 dakika geçerli.",
            ct);

        return Ok(new RegisterResponse(user.Id, user.Status.ToString(), "Kayıt alındı, e-posta doğrulama bekliyor."));
    }

    // GET /api/auth/verify-email?token=...
    [HttpGet("verify-email")]
    [EnableRateLimiting("auth-verify")]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { error = "missing_token" });

        var hash = _tokens.Sha256Hex(token);
        var otp = await _db.OtpCodes
            .FirstOrDefaultAsync(o => o.CodeHash == hash && o.UsedAt == null && o.ExpiresAt > DateTime.UtcNow, ct);
        if (otp is null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == otp.UserId, ct);
        if (user is null || user.Status != UserStatus.PendingEmail)
            return BadRequest(new { error = "invalid_state" });

        otp.UsedAt = DateTime.UtcNow;
        user.Status = UserStatus.PendingAdmin;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new VerifyEmailResponse(user.Status.ToString(), "E-posta doğrulandı, admin onayı bekleniyor."));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var input = req.UsernameOrEmail.Trim();
        var inputLower = input.ToLowerInvariant();

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == input || u.Email == inputLower, ct);

        // Generic error to avoid user enumeration
        if (user is null || !_hasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { error = "invalid_credentials" });

        if (user.Status != UserStatus.Active)
            return StatusCode(403, new { error = "account_not_active", status = user.Status.ToString() });

        var auth = await IssueTokens(user, ct);
        return Ok(auth);
    }

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var hash = _tokens.Sha256Hex(req.RefreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (existing is null)
            return Unauthorized(new { error = "invalid_token" });

        // Reuse detection: token revoked ama hâlâ kullanılıyor → tüm kullanıcı session'larını iptal et
        if (existing.RevokedAt is not null)
        {
            _logger.LogWarning("Refresh token reuse detected for user {UserId}, revoking all sessions", existing.UserId);
            var allActive = await _db.RefreshTokens
                .Where(r => r.UserId == existing.UserId && r.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var t in allActive)
            {
                t.RevokedAt = DateTime.UtcNow;
                t.RevokedReason = "reuse_detected";
            }
            await _db.SaveChangesAsync(ct);
            return Unauthorized(new { error = "token_reuse_detected" });
        }

        if (existing.ExpiresAt <= DateTime.UtcNow)
            return Unauthorized(new { error = "expired_token" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return Unauthorized(new { error = "account_not_active" });

        // Rotate
        existing.RevokedAt = DateTime.UtcNow;
        existing.RevokedReason = "rotated";

        var auth = IssueTokens(user, replacedTokenId: existing.Id);
        existing.ReplacedByTokenId = auth.RotatedFromId;
        await _db.SaveChangesAsync(ct);

        return Ok(auth.Response);
    }

    // POST /api/auth/change-password — JWT-authenticated user, T-024
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth-change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        if (!_hasher.Verify(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "current_password_incorrect" });

        if (req.NewPassword == req.CurrentPassword)
            return BadRequest(new { error = "new_password_same_as_current" });

        user.PasswordHash = _hasher.Hash(req.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        // Diğer cihazlardaki session'ları sonlandır — yeni şifreyle yeniden login zorunlu
        var active = await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in active)
        {
            t.RevokedAt = DateTime.UtcNow;
            t.RevokedReason = "password_changed";
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Password changed for user {UserId} ({Username})", userId, user.Username);
        return NoContent();
    }

    // POST /api/auth/logout
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var hash = _tokens.Sha256Hex(req.RefreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (existing is null || existing.RevokedAt is not null)
            return NoContent();

        existing.RevokedAt = DateTime.UtcNow;
        existing.RevokedReason = "logout";
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ──────────────────── Helpers ────────────────────

    private record IssuedTokens(AuthResponse Response, Guid RotatedFromId);

    private async Task<AuthResponse> IssueTokens(User user, CancellationToken ct)
    {
        var (access, refresh, expiresAt, _) = IssueTokensInternal(user);
        await _db.SaveChangesAsync(ct);
        return new AuthResponse(access.Value, access.ExpiresAt, refresh, expiresAt, user.Username, user.IsAdmin);
    }

    private IssuedTokens IssueTokens(User user, Guid replacedTokenId)
    {
        var (access, refresh, expiresAt, newId) = IssueTokensInternal(user);
        return new IssuedTokens(
            new AuthResponse(access.Value, access.ExpiresAt, refresh, expiresAt, user.Username, user.IsAdmin),
            newId);
    }

    private (AccessToken access, string refreshPlain, DateTime refreshExpires, Guid newTokenId) IssueTokensInternal(User user)
    {
        var access = _jwt.IssueAccessToken(user);

        var refreshPlain = _tokens.GenerateUrlSafeToken(32);
        var refreshExpires = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays);
        var rt = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokens.Sha256Hex(refreshPlain),
            ExpiresAt = refreshExpires,
        };
        _db.RefreshTokens.Add(rt);
        // Save deferred so caller can chain
        return (access, refreshPlain, refreshExpires, rt.Id);
    }
}
