namespace Mimir.Api.Middleware;

/// <summary>
/// T-045 — Force Update Gate (ADR-015).
/// Eski APK'lar her API çağrısında HTTP 426 alır → kullanılamaz.
/// Client-side check (T-039) yumuşaktı (offline'da bypass); bu backend authoritative.
///
/// Header zorunluluğu:
///   X-App-Version: <semver, örn. "0.1.0">
///   X-App-Platform: "android" | "ios" (opsiyonel, default android)
///
/// Muaf endpoint'ler: /api/auth/* (login, refresh — ileri APK'ya geçiş için), /api/app/version,
/// /health, /hubs/* (SignalR negotiation; korumayı REST katmanı sağlar).
/// </summary>
public class AppVersionGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AppVersionGateMiddleware> _logger;

    private static readonly string[] ExemptPrefixes =
    {
        "/api/auth/",
        "/api/app/",
        "/health",
        "/hubs/",
    };

    public AppVersionGateMiddleware(RequestDelegate next, ILogger<AppVersionGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx, IConfiguration config)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx);
            return;
        }

        var clientVersion = ctx.Request.Headers["X-App-Version"].FirstOrDefault();
        var platform = ctx.Request.Headers["X-App-Platform"].FirstOrDefault() ?? "android";
        var key = platform.Equals("ios", StringComparison.OrdinalIgnoreCase) ? "Ios" : "Android";
        var minVersion = config[$"MinAppVersion:{key}"] ?? "0.0.0";

        if (string.IsNullOrEmpty(clientVersion) || VersionLessThan(clientVersion, minVersion))
        {
            _logger.LogWarning(
                "App version gate: client {Client} < min {Min} for path {Path}",
                clientVersion ?? "(missing)", minVersion, path);

            ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            ctx.Response.ContentType = "application/json";
            var downloadUrl = config[$"AppDownloadUrl:{key}"] ?? "";
            await ctx.Response.WriteAsync(
                $"{{\"error\":\"app_version_too_old\",\"minVersion\":\"{minVersion}\",\"downloadUrl\":\"{downloadUrl}\",\"platform\":\"{platform}\"}}");
            return;
        }

        await _next(ctx);
    }

    private static bool VersionLessThan(string a, string b)
    {
        var ap = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var bp = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            var av = i < ap.Length ? ap[i] : 0;
            var bv = i < bp.Length ? bp[i] : 0;
            if (av < bv) return true;
            if (av > bv) return false;
        }
        return false;
    }
}
