using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mimir.Api.Data;
using Mimir.Api.Hubs;
using Mimir.Api.Middleware;
using Mimir.Api.Services.Email;
using Mimir.Api.Services.Push;
using Mimir.Api.Services.Security;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────── Logging ───────────────────────────
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

// ─────────────────────────── Database ──────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
builder.Services.AddDbContext<MimirDbContext>(opts => opts.UseNpgsql(connStr));

// ─────────────────────────── Redis ─────────────────────────────
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString missing");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConnStr));

// ─────────────────────────── Auth (JWT) ────────────────────────
var jwt = builder.Configuration.GetSection("Jwt");
var jwtKey = jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 chars");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // SignalR JWT — query string'den access_token okuma (WebSocket header'a token koyamıyor)
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Admin", p => p.RequireClaim("admin", "true"));
});

// ─────────────────────────── App Services ──────────────────────
builder.Services.AddSingleton<TokenGenerator>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<IJwtService, JwtService>();

// Email: Smtp:Host varsa gerçek SMTP, yoksa console fallback (mock)
var smtpHost = builder.Configuration["Smtp:Host"];
if (!string.IsNullOrWhiteSpace(smtpHost))
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

// Message crypto (DM at-rest, ADR-012)
builder.Services.AddSingleton<IMessageCrypto, AesGcmMessageCrypto>();

// Friendship gating (ADR-016)
builder.Services.AddScoped<IFriendshipChecker, FriendshipChecker>();

// FCM push (ADR-017) — signal-only
builder.Services.AddSingleton<IPushDispatcher, FcmDispatcher>();

// ─────────────────────────── Rate Limit (T-014) ─────────────────
// IP-based fixed window. Single-instance MVP'de yeterli; multi-replica olduğunda Redis-distributed'a taşınır.
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static RateLimitPartition<string> ByIp(HttpContext ctx, int permits, TimeSpan window)
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });
    }

    opts.AddPolicy("auth-register",        ctx => ByIp(ctx, 5,  TimeSpan.FromMinutes(1)));
    opts.AddPolicy("auth-login",           ctx => ByIp(ctx, 10, TimeSpan.FromMinutes(1)));
    opts.AddPolicy("auth-verify",          ctx => ByIp(ctx, 30, TimeSpan.FromMinutes(1)));
    opts.AddPolicy("auth-change-password", ctx => ByIp(ctx, 5,  TimeSpan.FromMinutes(1)));
    opts.AddPolicy("admin-invite",         ctx => ByIp(ctx, 20, TimeSpan.FromMinutes(1)));
    opts.AddPolicy("friend-request",       ctx => ByIp(ctx, 5,  TimeSpan.FromMinutes(1)));
});

// ─────────────────────────── SignalR ───────────────────────────
builder.Services.AddSignalR();

// ─────────────────────────── Web ───────────────────────────────
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Forwarded headers — nginx /mimir/ prefix yüzünden client IP doğru aktarılsın
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
    // Docker default subnet'lerini trust et — nginx container bunlardan birinde
    // Aksi halde X-Forwarded-For ignore edilir, rate limit nginx-IP-bazlı bucket olur (yanlış)
    opts.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
    opts.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
});

// ─────────────────────────── Health ────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(connStr, name: "postgres", tags: new[] { "ready" })
    .AddRedis(redisConnStr, name: "redis", tags: new[] { "ready" });

// ─────────────────────────── Pipeline ──────────────────────────
var app = builder.Build();

// ─────────────────────────── DB Migrate + Bootstrap Admin Seed ──
// MVP: startup migration. Tek-instance deploy'da OK.
// TODO Sprint #2 sonu: init-container pattern'i ya da CI step'i değerlendir (race condition / multi-replica güvenliği).
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<MimirDbContext>();
    db.Database.Migrate();

    // Admin:Bootstrap{Email,Password,Username} env'inde set ise + henüz hiç admin yoksa, oluştur.
    var config = sp.GetRequiredService<IConfiguration>();
    var bootEmail = config["Admin:BootstrapEmail"];
    var bootPassword = config["Admin:BootstrapPassword"];
    var bootUsername = config["Admin:BootstrapUsername"] ?? "admin";
    var bootLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");

    if (!string.IsNullOrWhiteSpace(bootEmail) && !string.IsNullOrWhiteSpace(bootPassword))
    {
        var hasAdmin = db.Users.Any(u => u.IsAdmin);
        if (!hasAdmin)
        {
            var hasher = sp.GetRequiredService<Mimir.Api.Services.Security.IPasswordHasher>();
            db.Users.Add(new Mimir.Api.Domain.User
            {
                Username = bootUsername,
                Email = bootEmail.Trim().ToLowerInvariant(),
                PasswordHash = hasher.Hash(bootPassword),
                Status = Mimir.Api.Domain.UserStatus.Active,
                IsAdmin = true,
            });
            db.SaveChanges();
            bootLogger.LogInformation("✅ Bootstrap admin user oluşturuldu: {Email}", bootEmail);
        }
        else
        {
            bootLogger.LogInformation("Admin user mevcut → bootstrap seed atlandı.");
        }
    }
    else
    {
        bootLogger.LogInformation("Admin:Bootstrap* config yok → bootstrap seed atlandı.");
    }

    // ADR-016 seed: FriendKey eksik kullanıcılara üret + mevcut DM çiftlerini Auto-Accepted friendship
    var tokens = sp.GetRequiredService<Mimir.Api.Services.Security.TokenGenerator>();

    // 1. FriendKey eksik olanlara üret
    var usersWithoutKey = db.Users.Where(u => u.FriendKey == null).ToList();
    foreach (var u in usersWithoutKey)
    {
        for (int i = 0; i < 5; i++)
        {
            var k = tokens.GenerateUrlSafeToken(byteSize: 9);
            if (!db.Users.Any(x => x.FriendKey == k))
            {
                u.FriendKey = k;
                break;
            }
        }
    }
    if (usersWithoutKey.Count > 0)
    {
        db.SaveChanges();
        bootLogger.LogInformation("FriendKey üretildi: {Count} kullanıcı", usersWithoutKey.Count);
    }

    // 2. Mevcut DM çiftlerini Auto-Accepted friendship'e taşı
    var msgPairs = db.Messages
        .Where(m => m.DeletedAt == null)
        .Select(m => new { m.SenderId, m.RecipientId })
        .ToList()
        .Select(p => p.SenderId.CompareTo(p.RecipientId) < 0
            ? (Lo: p.SenderId, Hi: p.RecipientId)
            : (Lo: p.RecipientId, Hi: p.SenderId))
        .Distinct()
        .ToList();

    int autoAccepted = 0;
    foreach (var p in msgPairs)
    {
        var exists = db.Friendships.Any(f =>
            (f.RequesterId == p.Lo && f.AddresseeId == p.Hi) ||
            (f.RequesterId == p.Hi && f.AddresseeId == p.Lo));
        if (!exists)
        {
            db.Friendships.Add(new Mimir.Api.Domain.Friendship
            {
                RequesterId = p.Lo,
                AddresseeId = p.Hi,
                Status = Mimir.Api.Domain.FriendshipStatus.Accepted,
                RespondedAt = DateTime.UtcNow,
            });
            autoAccepted++;
        }
    }
    if (autoAccepted > 0)
    {
        db.SaveChanges();
        bootLogger.LogInformation("Auto-Accepted friendship: {Count} çift (mevcut DM verisi)", autoAccepted);
    }
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

// T-045 / ADR-015: app version gate — eski APK her authenticated path'te 426 alır
app.UseMiddleware<AppVersionGateMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
// WebApplication, MapXxx'ten önce UseRouting'i otomatik ekler. UseRateLimiter Auth/AuthZ sonrası
// + Map* öncesi: route data hazır, rate limit policy attribute'ı okunabiliyor.
app.UseRateLimiter();

app.MapControllers();
app.MapHub<DmHub>("/hubs/dm").RequireAuthorization();

// /health           → fast liveness (sadece process up?)
// /health/ready     → DB + Redis bağlanabilir mi (k8s/orchestration için)
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
