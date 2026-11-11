using AllenBuilding.Domain.Tenancy;

namespace AllenBuilding.Application.Notifications;

public sealed record NotificationRow(Guid Id, Guid Version, string Kind, MessageState State, int Attempts,
    string? Error, DateTimeOffset CreatedAt, string? ResetRecipient, NotificationSubject? About = null);
// Which request a maintenance email was about, for a manager who may already open that request.
public sealed record NotificationSubject(Guid WorkOrderId, string Title, string Suite, string? Event, bool ToRequester);
public sealed record NotificationInboxPage(IReadOnlyList<NotificationRow> NeedsAttention,
    IReadOnlyList<NotificationRow> Recent, int ReviewPage, bool HasNextPage);
