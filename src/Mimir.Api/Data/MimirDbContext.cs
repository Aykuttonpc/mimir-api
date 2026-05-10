using Microsoft.EntityFrameworkCore;
using Mimir.Api.Domain;

namespace Mimir.Api.Data;

public class MimirDbContext : DbContext
{
    public MimirDbContext(DbContextOptions<MimirDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AdminApproval> AdminApprovals => Set<AdminApproval>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserDeviceToken> UserDeviceTokens => Set<UserDeviceToken>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);

            e.Property(x => x.Username).HasMaxLength(50).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20);                          // nullable, ADR-010
            e.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.IsAdmin).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.FriendKey).HasMaxLength(20);
            // Sprint #11: presence — DB persist (offline kullanıcılar için son görülme).
            e.Property(x => x.LastSeenAt);

            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            // Phone nullable: PostgreSQL multiple-NULL kabul eder, sadece dolu değerler unique olur.
            e.HasIndex(x => x.Phone).IsUnique();
            e.HasIndex(x => x.FriendKey).IsUnique();
        });

        mb.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(x => x.Id);

            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();        // SHA-256 hex (64 char)
            e.Property(x => x.Note).HasMaxLength(255);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.IssuedByUserId);
            e.HasIndex(x => x.RedeemedByUserId);
        });

        mb.Entity<OtpCode>(e =>
        {
            e.ToTable("otp_codes");
            e.HasKey(x => x.Id);

            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.AttemptCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);          // expired temizliği için
        });

        mb.Entity<AdminApproval>(e =>
        {
            e.ToTable("admin_approvals");
            e.HasKey(x => x.Id);

            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.DecidedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ApprovedByUserId);
        });

        mb.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);

            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.RevokedReason).HasMaxLength(50);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.Ignore(x => x.IsActive);              // computed property — DB'de kolon değil

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
        });

        mb.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);

            e.Property(x => x.Iv).IsRequired();
            e.Property(x => x.Ciphertext).IsRequired();
            e.Property(x => x.Tag).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            // Conversation listing — sender görünüm
            e.HasIndex(x => new { x.SenderId, x.RecipientId, x.CreatedAt });
            // Recipient görünüm + unread query
            e.HasIndex(x => new { x.RecipientId, x.SenderId, x.CreatedAt });
            // Unread count query
            e.HasIndex(x => new { x.RecipientId, x.ReadAt });
        });

        mb.Entity<Friendship>(e =>
        {
            e.ToTable("friendships");
            e.HasKey(x => x.Id);

            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => new { x.RequesterId, x.AddresseeId });
            e.HasIndex(x => new { x.AddresseeId, x.RequesterId });
            e.HasIndex(x => x.Status);
        });

        mb.Entity<UserDeviceToken>(e =>
        {
            e.ToTable("user_device_tokens");
            e.HasKey(x => x.Id);

            e.Property(x => x.FcmToken).HasMaxLength(512).IsRequired();
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.LastSeenAt).HasDefaultValueSql("now()");

            e.HasIndex(x => x.UserId);
            // Aynı token aynı user'da bir kez (FCM aynı cihaza aynı token verir,
            // app yeniden yüklendiğinde token rotate olur — eski silinir).
            e.HasIndex(x => x.FcmToken).IsUnique();
        });
    }
}
