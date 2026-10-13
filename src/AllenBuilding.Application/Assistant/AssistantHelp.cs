using AllenBuilding.Application.Billing;
using AllenBuilding.Application.Knowledge;
using AllenBuilding.Application.Maintenance;

namespace AllenBuilding.Application.Assistant;

public sealed record AssistantPolicy(bool Enabled, bool TenantHelpEnabled = false, bool BillingEnabled = false);
public sealed record AssistantLink(string Label, string Path);
public sealed record AssistantAnswer(AssistantModelCode Code, string Text = "", IReadOnlyList<HelpSource>? Sources = null,
    AssistantLink? Next = null, AssistantTenantScope? Scope = null, IReadOnlyList<AssistantSource>? ContextualSources = null,
    IReadOnlyList<RequestSummary>? Requests = null, IReadOnlyList<BillingAccount>? Billing = null);

// Stateless first milestone: never accepts or persists a client-supplied transcript.
// Private questions explicitly select one original membership and carry it through output admission.
public sealed class AssistantHelp(KnowledgeActions knowledge, IAssistantModel model, AssistantPolicy policy, IAssistantTenantAccess access,
    MaintenanceActions maintenance, BillingActions billing)
{
    public async Task<AssistantAnswer> AskAsync(string? question, CancellationToken cancellationToken = default, Guid? membershipId = null)
    {
        if (!policy.Enabled) return new(AssistantModelCode.Unavailable);
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000) return new(AssistantModelCode.Invalid);
        AssistantTenantScope? scope = null;
        if (membershipId is { } member)
        {
            if (!policy.TenantHelpEnabled) return new(AssistantModelCode.Denied);
            scope = await access.CaptureAsync(member, cancellationToken);
            if (scope is null) return new(AssistantModelCode.Denied);
        }
        var terms = question.Split([' ', '\n', '\r', '.', ',', '?', '!', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2).Select(x => x.ToLowerInvariant()).Distinct().Take(20).ToArray();
        var available = scope is null ? await knowledge.FindAsync(KnowledgeScope.Public, cancellationToken: cancellationToken)
            : await knowledge.FindForMembershipAsync(scope.MembershipId, cancellationToken: cancellationToken);
        var sources = available.Select(x => new { Source = x, Score = terms.Count(t => x.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || x.Body.Contains(t, StringComparison.OrdinalIgnoreCase)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Source.ArticleId).Take(5).Select(x => x.Source).ToArray();
        var contextual = sources.Select(x => new AssistantSource(x.ArticleId, x.RevisionId,
            x.Title[..Math.Min(x.Title.Length, 180)], x.Body[..Math.Min(x.Body.Length, 800)])).ToArray();
        var reply = await model.AnswerAsync(new(question.Trim(), contextual, scope is not null), cancellationToken);
        if (reply.Code != AssistantModelCode.Success) return new(reply.Code);
        if (reply.Answer.Length is < 1 or > 3000 || reply.Sources is null || reply.Sources.Count > 5 ||
            reply.Sources.Any(id => !sources.Any(x => x.ArticleId == id))) return new(AssistantModelCode.Invalid);
        // A draft replacement, withdrawal or changed audience during generation invalidates the whole answer.
        foreach (var source in sources)
        {
            var fresh = scope is null ? await knowledge.FindAsync(KnowledgeScope.Public, id: source.ArticleId, cancellationToken: cancellationToken)
                : await knowledge.FindForMembershipAsync(scope.MembershipId, source.ArticleId, cancellationToken);
            if (fresh.Count != 1 || fresh[0].RevisionId != source.RevisionId) return new(AssistantModelCode.Unavailable);
        }
        var next = reply.Route switch
        {
            "help" => new AssistantLink("Read building help", scope is null ? "/Knowledge" : "/Knowledge/Tenant"),
            "requests" when scope is not null => new("Your maintenance requests", "/Maintenance"),
            "new_request" when scope is not null => new("Report a maintenance issue", "/Maintenance/Create"),
            "billing" or "autopay" when scope is not null && policy.BillingEnabled => new("Your billing records and account options", "/Billing"),
            "billing" or "autopay" when scope is not null => new("Open your office", "/Portal"),
            "login" or "requests" or "new_request" or "billing" or "autopay" => new("Sign in to your office", "/Account/Login"),
            _ => null
        };
        if (next is null) return new(AssistantModelCode.Invalid);
        // Critical record values are retrieved after generation and never sent to the model in this stage.
        // SQL scopes the original membership before projection and bounds, even for a manager/tenant account.
        IReadOnlyList<RequestSummary>? requests = scope is not null && reply.Route == "requests"
            ? (await maintenance.ListForMembershipAsync(scope.MembershipId, cancellationToken)).Take(5).ToArray() : null;
        IReadOnlyList<BillingAccount>? accounts = scope is not null && policy.BillingEnabled && (reply.Route is "billing" or "autopay")
            ? await billing.ReadForMembershipAsync(scope.MembershipId, cancellationToken) : null;
        return new(AssistantModelCode.Success, reply.Answer, sources.Where(x => reply.Sources.Contains(x.ArticleId)).ToArray(), next, scope, contextual, requests, accounts);
    }
}
