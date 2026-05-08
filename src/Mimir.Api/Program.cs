using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mimir.Api.Data;
using Mimir.Api.Hubs;
using Mimir.Api.Services.Email;
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
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

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
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

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
