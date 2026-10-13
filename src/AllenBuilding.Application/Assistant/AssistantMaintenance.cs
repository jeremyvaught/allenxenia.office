using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Application.Assistant;

public sealed record AssistantDraft(AssistantReviewKind Kind, string Title = "", string Description = "", string Location = "", string Comment = "");
public sealed record AssistantReviewClaim(Guid Id, Guid ConversationId, Guid Version);
public sealed record AssistantReviewView(AssistantMaintenanceReview Review, AssistantTenantScope Scope, IReadOnlyList<AssistantSource> Sources, string? TargetTitle = null);
public sealed record AssistantReviewResult(MaintenanceCode Code, AssistantReviewView? View = null);

public interface IAssistantReviewStore
{
    Task<AssistantReviewResult> PrepareAsync(Guid conversation, AssistantTenantScope scope, AssistantDraft draft, RequestDetails? target, IReadOnlyList<AssistantSource> sources, CancellationToken cancellationToken);
    Task<AssistantReviewView?> ReadAsync(Guid id, Guid conversation, CancellationToken cancellationToken);
    Task<MaintenanceCode> EditAsync(AssistantReviewClaim claim, AssistantDraft draft, bool cancel, CancellationToken cancellationToken);
}

// Preparation is model-callable; confirmation is exclusively a separate protected browser POST.
public sealed class AssistantMaintenance(IAssistantReviewStore store, MaintenanceActions maintenance)
{
    public Task<AssistantReviewResult> PrepareAsync(Guid conversation, AssistantTenantScope scope, AssistantDraft draft, RequestDetails? target,
        IReadOnlyList<AssistantSource> sources, CancellationToken cancellationToken) => store.PrepareAsync(conversation, scope, draft, target, sources, cancellationToken);
    public Task<AssistantReviewView?> ReadAsync(Guid id, Guid conversation, CancellationToken cancellationToken) => store.ReadAsync(id, conversation, cancellationToken);
    public Task<MaintenanceCode> EditAsync(AssistantReviewClaim claim, AssistantDraft draft, bool cancel, CancellationToken cancellationToken)
        => store.EditAsync(claim, draft, cancel, cancellationToken);
    public async Task<MaintenanceResult> ConfirmAsync(AssistantReviewClaim claim, Guid operationId, CancellationToken cancellationToken)
    {
        var view = await store.ReadAsync(claim.Id, claim.ConversationId, cancellationToken);
        if (view is null) return new(MaintenanceCode.Unavailable);
        var review = view.Review;
        if (review.Version != claim.Version || review.OperationId != operationId) return new(MaintenanceCode.Conflict);
        return review.Kind == AssistantReviewKind.Request
            ? await maintenance.SubmitReviewedAsync(Request(review), claim, cancellationToken)
            : await maintenance.CommentReviewedAsync(Comment(review), claim, cancellationToken);
    }
    public static SubmitRequest Request(AssistantMaintenanceReview review) => new(review.OperationId, review.MembershipId, review.Title, review.Description, review.Location);
    public static ChangeRequest Comment(AssistantMaintenanceReview review) => new(review.OperationId, review.WorkOrderId!.Value, review.WorkOrderVersion!.Value, MaintenanceAction.Comment, Note: review.Comment);
    public static string Hash(AssistantMaintenanceReview review) => review.Kind == AssistantReviewKind.Request
        ? MaintenanceActions.Fingerprint(Request(review)) : MaintenanceActions.Fingerprint(new { command = Comment(review), management = false });
}
