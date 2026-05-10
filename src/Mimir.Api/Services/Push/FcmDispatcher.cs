using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Data;

namespace Mimir.Api.Services.Push;

// ADR-017: FCM signal-only. Hiçbir içerik FCM'e gitmez — sadece "uyan" sinyali.
// Mobile uyanınca Mimir API'sinden mesajı çeker.
public class FcmDispatcher : IPushDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FcmDispatcher> _logger;
    private readonly FirebaseMessaging? _messaging;

    public FcmDispatcher(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<FcmDispatcher> logger)
    {
        _scopes = scopes;
        _logger = logger;

        var path = config["Firebase:ServiceAccountPath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("Firebase:ServiceAccountPath geçersiz veya dosya yok ({Path}) — FCM disabled", path);
            return;
        }

        // FirebaseApp.Create idempotent değil; aynı app iki kez yaratılırsa exception atar.
        // ProjectId credential'dan otomatik çıkmıyor — JSON'dan parse edip explicit ver (log temizliği).
        var projectId = ReadProjectIdFromKeyFile(path);
        var appOptions = new AppOptions { Credential = GoogleCredential.FromFile(path) };
        if (!string.IsNullOrEmpty(projectId)) appOptions.ProjectId = projectId;

        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(appOptions);
        _messaging = FirebaseMessaging.GetMessaging(app);
        _logger.LogInformation("FCM dispatcher initialized: project={ProjectId}", app.Options.ProjectId);
    }

    public Task SendNewMessageSignalAsync(Guid recipientUserId, Guid senderUserId, CancellationToken ct = default) =>
        SendInternalAsync(recipientUserId, BuildMessagePayloadAsync, senderUserId, "", ct);

    public Task SendIncomingCallSignalAsync(Guid recipientUserId, Guid callerUserId, string callerUsername, CancellationToken ct = default) =>
        SendInternalAsync(recipientUserId, BuildCallPayloadAsync, callerUserId, callerUsername, ct);

    private Func<Guid, string, Task<Dictionary<string, string>>> BuildMessagePayloadAsync => (senderId, _) => Task.FromResult(
        new Dictionary<string, string>
        {
            ["type"] = "newMessage",
            ["senderUserId"] = senderId.ToString(),
        });

    private Func<Guid, string, Task<Dictionary<string, string>>> BuildCallPayloadAsync => (callerId, callerUsername) => Task.FromResult(
        new Dictionary<string, string>
        {
            ["type"] = "callOffer",
            ["callerUserId"] = callerId.ToString(),
            ["callerUsername"] = callerUsername,
        });

    private async Task SendInternalAsync(
        Guid recipientUserId,
        Func<Guid, string, Task<Dictionary<string, string>>> payloadBuilder,
        Guid otherId,
        string extraStr,
        CancellationToken ct)
    {
        if (_messaging is null) return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MimirDbContext>();

        var tokens = await db.UserDeviceTokens
            .Where(t => t.UserId == recipientUserId)
            .Select(t => t.FcmToken)
            .ToListAsync(ct);

        if (tokens.Count == 0) return;

        var data = await payloadBuilder(otherId, extraStr);
        // newMessage için senderUsername'i DB'den ekle (call için zaten payload'da var)
        if (data["type"] == "newMessage")
        {
            var senderUsername = await db.Users
                .Where(u => u.Id == otherId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(ct) ?? "";
            data["senderUsername"] = senderUsername;
        }

        var msg = new MulticastMessage
        {
            Tokens = tokens,
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,    // Doze mode + locked screen wakeup
            },
        };

        try
        {
            var resp = await _messaging.SendEachForMulticastAsync(msg, ct);
            if (resp.FailureCount > 0)
            {
                // Geçersiz token'ları temizle (app silindi / token rotate oldu)
                var invalid = new List<string>();
                for (int i = 0; i < resp.Responses.Count; i++)
                {
                    var r = resp.Responses[i];
                    if (r.IsSuccess) continue;

                    var code = r.Exception?.MessagingErrorCode;
                    if (code is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch)
                        invalid.Add(tokens[i]);
                    else
                        _logger.LogWarning(r.Exception, "FCM send fail (token {Token}): {Code}", Truncate(tokens[i]), code);
                }

                if (invalid.Count > 0)
                {
                    await db.UserDeviceTokens
                        .Where(t => invalid.Contains(t.FcmToken))
                        .ExecuteDeleteAsync(ct);
                    _logger.LogInformation("FCM stale token temizlendi: {Count}", invalid.Count);
                }
            }
        }
        catch (Exception ex)
        {
            // Push hatası mesaj kaydını bozmamalı — log + sessiz geç.
            _logger.LogError(ex, "FCM send fail (recipient {RecipientId})", recipientUserId);
        }
    }

    private static string Truncate(string s) => s.Length <= 16 ? s : s[..16] + "...";

    private static string? ReadProjectIdFromKeyFile(string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("project_id", out var pid) ? pid.GetString() : null;
        }
        catch { return null; }
    }
}
