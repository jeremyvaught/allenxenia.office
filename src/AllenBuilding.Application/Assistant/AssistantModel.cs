namespace AllenBuilding.Application.Assistant;

public sealed record AssistantSource(Guid ArticleId, Guid RevisionId, string Title, string Excerpt);
public sealed record AssistantModelInput(string Question, IReadOnlyList<AssistantSource> Sources, bool Tenant);
public enum AssistantModelCode { Success, Unavailable, Invalid, Denied }
public sealed record AssistantModelReply(AssistantModelCode Code, string Answer = "", IReadOnlyList<Guid>? Sources = null,
    string Route = "help", int InputTokens = 0, int OutputTokens = 0);

// The provider sees bounded authorized excerpts and proposes text/fixed routes only.
// No identity, database handle, arbitrary destination or executable command crosses this port.
public interface IAssistantModel
{
    Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken);
}

public sealed class DisabledAssistantModel : IAssistantModel
{
    public Task<AssistantModelReply> AnswerAsync(AssistantModelInput input, CancellationToken cancellationToken)
        => Task.FromResult(new AssistantModelReply(AssistantModelCode.Unavailable));
}
