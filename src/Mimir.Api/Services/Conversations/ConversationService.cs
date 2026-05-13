using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;

namespace Mimir.Api.Services.Conversations;

/// <summary>
/// Sprint #14: Conversation yardımcı servisi — membership, idempotent DM lookup,
/// authorize helper'ları. Controller + Hub aynı mantığı paylaşır.
/// </summary>
public interface IConversationService
{
    Task<ConversationMember?> GetActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<Conversation?> FindDmAsync(Guid userA, Guid userB, CancellationToken ct = default);
    Task<List<Guid>> GetMemberIdsAsync(Guid conversationId, CancellationToken ct = default);
    Task TouchActivityAsync(Guid conversationId, CancellationToken ct = default);
    string ResolveSignalRGroup(Guid conversationId) => $"conv-{conversationId}";
}

public class ConversationService : IConversationService
{
    private readonly MimirDbContext _db;
    public ConversationService(MimirDbContext db) => _db = db;

    public Task<ConversationMember?> GetActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
        => _db.ConversationMembers
            .FirstOrDefaultAsync(m =>
                m.ConversationId == conversationId &&
                m.UserId == userId &&
                m.LeftAt == null, ct);

    /// <summary>
    /// İki user arasında zaten DM Conversation varsa onu döner — idempotent createDm için.
    /// </summary>
    public async Task<Conversation?> FindDmAsync(Guid userA, Guid userB, CancellationToken ct = default)
    {
        var convIds = await _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userA && m.LeftAt == null)
            .Select(m => m.ConversationId)
            .ToListAsync(ct);
        if (convIds.Count == 0) return null;

        return await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Type == ConversationType.Dm && convIds.Contains(c.Id))
            .Where(c => _db.ConversationMembers.Any(m =>
                m.ConversationId == c.Id && m.UserId == userB && m.LeftAt == null))
            .FirstOrDefaultAsync(ct);
    }

    public Task<List<Guid>> GetMemberIdsAsync(Guid conversationId, CancellationToken ct = default)
        => _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public Task TouchActivityAsync(Guid conversationId, CancellationToken ct = default)
        => _db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.LastActivityAt, DateTime.UtcNow), ct);
}
