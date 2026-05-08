using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Mimir.Api.Hubs;

/// <summary>
/// DM hub — gerçek implementasyon Sprint #4 (T-* gelecek).
/// Şimdilik sadece JWT-protected bağlantı placeholder'ı.
/// ADR-005: server-side encryption (AES-256 at-rest).
/// </summary>
[Authorize]
public class DmHub : Hub
{
    public override Task OnConnectedAsync()
    {
        // TODO Sprint #4: kullanıcı id'sini Context.User'dan al, online presence track et.
        return base.OnConnectedAsync();
    }
}
