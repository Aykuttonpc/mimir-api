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

            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            // Phone nullable: PostgreSQL multiple-NULL kabul eder, sadece dolu değerler unique olur.
            e.HasIndex(x => x.Phone).IsUnique();
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
    }
}
