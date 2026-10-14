using AllenBuilding.Domain.Tenancy;

namespace AllenBuilding.Application.Notifications;

public sealed record NotificationRow(Guid Id, Guid Version, string Kind, MessageState State, int Attempts,
    string? Error, DateTimeOffset CreatedAt, string? ResetRecipient);
public sealed record NotificationInboxPage(IReadOnlyList<NotificationRow> NeedsAttention,
    IReadOnlyList<NotificationRow> Recent, int ReviewPage, bool HasNextPage);
