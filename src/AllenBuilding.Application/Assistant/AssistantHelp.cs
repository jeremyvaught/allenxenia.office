using AllenBuilding.Application.Knowledge;

namespace AllenBuilding.Application.Assistant;

public sealed record AssistantPolicy(bool Enabled);
public sealed record AssistantLink(string Label, string Path);
public sealed record AssistantAnswer(AssistantModelCode Code, string Text = "", IReadOnlyList<HelpSource>? Sources = null,
    AssistantLink? Next = null);

// Stateless first milestone: never accepts or persists a client-supplied transcript.
// Every answer starts with current published public sources; private capabilities follow separately.
public sealed class AssistantHelp(KnowledgeActions knowledge, IAssistantModel model, AssistantPolicy policy)
{
    public async Task<AssistantAnswer> AskAsync(string? question, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) return new(AssistantModelCode.Unavailable);
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000) return new(AssistantModelCode.Invalid);
        var terms = question.Split([' ', '\n', '\r', '.', ',', '?', '!', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2).Select(x => x.ToLowerInvariant()).Distinct().Take(20).ToArray();
        var available = await knowledge.FindAsync(KnowledgeScope.Public, cancellationToken: cancellationToken);
        var sources = available.Select(x => new { Source = x, Score = terms.Count(t => x.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || x.Body.Contains(t, StringComparison.OrdinalIgnoreCase)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Source.ArticleId).Take(5).Select(x => x.Source).ToArray();
        var reply = await model.AnswerAsync(new(question.Trim(), sources.Select(x => new AssistantSource(x.ArticleId, x.RevisionId,
            x.Title[..Math.Min(x.Title.Length, 180)], x.Body[..Math.Min(x.Body.Length, 800)])).ToArray(), false), cancellationToken);
        if (reply.Code != AssistantModelCode.Success) return new(reply.Code);
        if (reply.Answer.Length is < 1 or > 3000 || reply.Sources is null || reply.Sources.Count > 5 ||
            reply.Sources.Any(id => !sources.Any(x => x.ArticleId == id))) return new(AssistantModelCode.Invalid);
        // A draft replacement, withdrawal or changed audience during generation invalidates the whole answer.
        foreach (var source in sources)
        {
            var fresh = await knowledge.FindAsync(KnowledgeScope.Public, id: source.ArticleId, cancellationToken: cancellationToken);
            if (fresh.Count != 1 || fresh[0].RevisionId != source.RevisionId) return new(AssistantModelCode.Unavailable);
        }
        var next = reply.Route switch
        {
            "help" => new AssistantLink("Read building help", "/Knowledge"),
            "login" or "requests" or "new_request" or "billing" or "autopay" => new("Sign in to your office", "/Account/Login"),
            _ => null
        };
        if (next is null) return new(AssistantModelCode.Invalid);
        return new(AssistantModelCode.Success, reply.Answer, sources.Where(x => reply.Sources.Contains(x.ArticleId)).ToArray(), next);
    }
}
