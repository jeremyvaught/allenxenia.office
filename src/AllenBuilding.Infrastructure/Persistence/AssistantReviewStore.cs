using System.Text.Json;
using AllenBuilding.Application.Access;
using AllenBuilding.Application.Assistant;
using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;

namespace AllenBuilding.Infrastructure.Persistence;

public sealed class AssistantReviewStore(OfficeDbContext db, ICurrentActor current, AssistantTenantAccess access, TimeProvider time) : IAssistantReviewStore
{
    private sealed record SourceReference(Guid ArticleId, Guid RevisionId);
    private static IReadOnlyList<AssistantSource>? Sources(AssistantMaintenanceReview review)
    {
        try
        {
            var values = JsonSerializer.Deserialize<SourceReference[]>(review.SourcesJson);
            return values is null || values.Length > 5 || values.Any(x => x.ArticleId == Guid.Empty || x.RevisionId == Guid.Empty)
                ? null : values.Select(x => new AssistantSource(x.ArticleId, x.RevisionId, "", "")).ToArray();
        }
        catch (JsonException) { return null; }
    }
    private async Task<bool> Valid(AssistantMaintenanceReview review, CancellationToken token)
    {
        var sources = Sources(review); if (sources is null) return false;
        return await access.ValidateLockedAsync(new(review.OwnerId, review.MembershipId, review.MembershipVersion, review.SecurityStamp, ""), sources, token);
    }
    public async Task<AssistantReviewResult> PrepareAsync(Guid conversation, AssistantTenantScope scope, AssistantDraft draft, RequestDetails? target,
        IReadOnlyList<AssistantSource> sources, CancellationToken token)
    {
        if (sources.Count > 5 || (draft.Kind == AssistantReviewKind.Comment && target is null) ||
            (draft.Kind == AssistantReviewKind.Request && target is not null)) return new(MaintenanceCode.ValidationFailed);
        AssistantMaintenanceReview review;
        try
        {
            review = AssistantMaintenanceReview.Prepare(conversation, scope.UserId, scope.MembershipId, scope.Version, scope.SecurityStamp,
                draft.Kind, draft.Title, draft.Description, draft.Location, draft.Comment, target?.Id, target?.Version,
                JsonSerializer.Serialize(sources.Select(x => new SourceReference(x.ArticleId, x.RevisionId))), time.GetUtcNow());
            review.BindHash(AssistantMaintenance.Hash(review));
        }
        catch (ArgumentException) { return new(MaintenanceCode.ValidationFailed); }
        await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        await AuthorityLocks.UserAsync(db, scope.UserId, token); await AuthorityLocks.MembershipAsync(db, scope.MembershipId, token);
        if (!await Valid(review, token)) return new(MaintenanceCode.Unavailable);
        if (target is not null)
        {
            await AuthorityLocks.WorkOrderAsync(db, target.Id, token);
            if (!await db.WorkOrders.AnyAsync(x => x.Id == target.Id && x.RequesterMembershipId == scope.MembershipId &&
                x.RequesterUserId == scope.UserId && x.Version == target.Version, token)) return new(MaintenanceCode.Conflict);
        }
        var existing = await db.AssistantMaintenanceReviews.AsNoTracking().SingleOrDefaultAsync(x => x.ConversationId == conversation && x.OwnerId == scope.UserId && x.MembershipId == scope.MembershipId, token);
        if (existing is not null)
        {
            if (!await Valid(existing, token)) return new(MaintenanceCode.Unavailable);
            if (!existing.IsReady(time.GetUtcNow()) || existing.Kind != review.Kind || existing.Title != review.Title || existing.Description != review.Description ||
                existing.Location != review.Location || existing.Comment != review.Comment || existing.WorkOrderId != review.WorkOrderId ||
                existing.WorkOrderVersion != review.WorkOrderVersion || existing.SourcesJson != review.SourcesJson) return new(MaintenanceCode.Conflict);
            return new(MaintenanceCode.Success, new(existing, scope, Sources(existing)!, target?.Title));
        }
        if (await db.AssistantMaintenanceReviews.AnyAsync(x => x.ConversationId == conversation, token)) return new(MaintenanceCode.Unavailable);
        db.AssistantMaintenanceReviews.Add(review);
        try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        catch (DbUpdateException exception) when (Collision(exception)) { return new(MaintenanceCode.Conflict); }
        return new(MaintenanceCode.Success, new(review, scope, Sources(review)!, target?.Title));
    }
    public async Task<AssistantReviewView?> ReadAsync(Guid id, Guid conversation, CancellationToken token)
    {
        var actor = await current.GetAsync(token); if (!MaintenanceActions.CanUse(actor, false)) return null;
        var member = await db.AssistantMaintenanceReviews.AsNoTracking().Where(x => x.Id == id && x.ConversationId == conversation && x.OwnerId == actor!.UserId)
            .Select(x => (Guid?)x.MembershipId).SingleOrDefaultAsync(token);
        if (member is null) return null;
        await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        await AuthorityLocks.UserAsync(db, actor!.UserId, token); await AuthorityLocks.MembershipAsync(db, member.Value, token);
        var review = await db.AssistantMaintenanceReviews.AsNoTracking().SingleAsync(x => x.Id == id, token);
        if (!await Valid(review, token)) return null;
        var scope = (await access.CaptureAsync(member.Value, token))!;
        var title = review.WorkOrderId is { } request ? await db.WorkOrders.Where(x => x.Id == request && x.RequesterMembershipId == member && x.RequesterUserId == actor.UserId)
            .Select(x => x.Title).SingleOrDefaultAsync(token) : null;
        if (review.Kind == AssistantReviewKind.Comment && title is null) return null;
        await transaction.CommitAsync(token); return new(review, scope, Sources(review)!, title);
    }
    public async Task<MaintenanceCode> EditAsync(AssistantReviewClaim claim, AssistantDraft draft, bool cancel, CancellationToken token)
    {
        var view = await ReadAsync(claim.Id, claim.ConversationId, token); if (view is null) return MaintenanceCode.Unavailable;
        await using var transaction = await AuthorityLocks.BeginAsync(db, token);
        await AuthorityLocks.UserAsync(db, view.Scope.UserId, token); await AuthorityLocks.MembershipAsync(db, view.Scope.MembershipId, token);
        var review = await db.AssistantMaintenanceReviews.SingleAsync(x => x.Id == claim.Id, token); await db.Entry(review).ReloadAsync(token);
        if (!await Valid(review, token)) return MaintenanceCode.Unavailable;
        if (review.Version != claim.Version || review.State != AssistantReviewState.Ready) return MaintenanceCode.Conflict;
        try
        {
            if (cancel) review.Cancel();
            else
            {
                if (draft.Kind != review.Kind) return MaintenanceCode.ValidationFailed;
                review.Edit(draft.Title, draft.Description, draft.Location, draft.Comment, time.GetUtcNow()); review.BindHash(AssistantMaintenance.Hash(review));
            }
        }
        catch (ArgumentException) { return MaintenanceCode.ValidationFailed; }
        catch (InvalidOperationException) { return MaintenanceCode.Conflict; }
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return MaintenanceCode.Success;
    }

    // Called ONLY inside the ordinary maintenance write transaction, after original resource locks.
    public async Task<AssistantMaintenanceReview?> AdmitAsync(Actor actor, AssistantReviewClaim claim, Guid membership, Guid operation,
        string hash, AssistantReviewKind kind, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) return null;
        var review = await db.AssistantMaintenanceReviews.SingleOrDefaultAsync(x => x.Id == claim.Id && x.ConversationId == claim.ConversationId && x.OwnerId == actor.UserId, token);
        if (review is null) return null;
        await db.Entry(review).ReloadAsync(token);
        if (review.MembershipId != membership || review.OperationId != operation || review.Version != claim.Version || review.PayloadHash != hash || review.Kind != kind ||
            (review.State != AssistantReviewState.Completed && !review.IsReady(time.GetUtcNow())) || !await Valid(review, token)) return null;
        return review;
    }
    private static bool Collision(DbUpdateException exception) => exception.InnerException is
        MySql.Data.MySqlClient.MySqlException { Number: 1062 } or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };
}
