using AllenBuilding.Application.Maintenance;
using AllenBuilding.Domain.Knowledge;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.DemoSeed;

// A moment relative to the run: a working-hours clock time some business days back, or a few hours ago.
// Ago is kept under six hours, so every Day (at most yesterday evening) is earlier than every Ago.
internal readonly record struct When(int BusinessDays, int Hour, int Minute, TimeSpan? Before)
{
    public static When Day(int businessDaysAgo, int hour, int minute) => businessDaysAgo >= 1 && hour is >= 7 and <= 18 && minute is >= 0 and < 60
        ? new(businessDaysAgo, hour, minute, null)
        : throw new ArgumentException("Use one or more business days and a working hour.");

    public static When Ago(int hours, int minutes = 0) => hours is >= 0 and < 6 && minutes is >= 0 and < 60 && hours + minutes > 0
        ? new(0, 0, 0, new TimeSpan(hours, minutes, 0))
        : throw new ArgumentException("Use less than six hours.");

    public DateTimeOffset Resolve(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (Before is { } before)
            return now - before;
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var counted = 0; counted < BusinessDays;)
        {
            date = date.AddDays(-1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                counted++;
        }
        var local = date.AddHours(Hour).AddMinutes(Minute);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

internal enum Mail { Send, Hold, Fail }

internal sealed record Person(string Key, string Name, string Email);
internal sealed record TenantSeed(Person Contact, string Business, string Suite, When Invited, When Accepted);
internal sealed record InviteSeed(Person Contact, string Business, string Suite, When Invited);
internal sealed record ContractorSeed(string Name, Trade Trade, string Email, string Phone, When Added, When? Deactivated = null);
internal sealed record ArticleSeed(string Title, ArticleAudience Audience, Person Editor, When Drafted, When? Published, string Body);

// One step in a request's life. By is null when the tenant who filed the request acts.
internal sealed record Step(When At, Person? By, MaintenanceAction Action, Priority? Priority = null, Trade? Trade = null,
    string? Note = null, bool Internal = false, string? Contractor = null, Mail Mail = Mail.Send);

internal sealed class RequestScript(Person tenant, string title, string description, string location, When submitted)
{
    public Person Tenant => tenant;
    public string Title => title;
    public string Description => description;
    public string Location => location;
    public When Submitted => submitted;
    public List<Step> Steps { get; } = [];

    public RequestScript Triage(Person by, When at, Priority priority, Trade trade) => Add(new(at, by, MaintenanceAction.Triage, priority, trade));
    public RequestScript AssignSelf(Person by, When at) => Add(new(at, by, MaintenanceAction.AssignSelf));
    public RequestScript Assign(Person by, When at, string contractor) => Add(new(at, by, MaintenanceAction.Assign, Contractor: contractor));
    public RequestScript Start(Person by, When at) => Add(new(at, by, MaintenanceAction.Start));
    public RequestScript Complete(Person by, When at, string resolution) => Add(new(at, by, MaintenanceAction.Complete, Note: resolution));
    public RequestScript Close(Person by, When at) => Add(new(at, by, MaintenanceAction.Close));
    public RequestScript Cancel(Person by, When at, string reason) => Add(new(at, by, MaintenanceAction.Cancel, Note: reason));
    public RequestScript Reopen(Person by, When at, string reason) => Add(new(at, by, MaintenanceAction.Reopen, Note: reason));
    public RequestScript Says(When at, string comment) => Add(new(at, null, MaintenanceAction.Comment, Note: comment));
    public RequestScript Reply(Person by, When at, string comment) => Add(new(at, by, MaintenanceAction.Comment, Note: comment));
    public RequestScript Note(Person by, When at, string note) => Add(new(at, by, MaintenanceAction.Comment, Note: note, Internal: true));
    // The email for the last step stays waiting to send, or fails, instead of being marked delivered.
    public RequestScript HoldMail() => Last(Mail.Hold);
    public RequestScript FailMail() => Last(Mail.Fail);

    private RequestScript Add(Step step) { Steps.Add(step); return this; }
    private RequestScript Last(Mail mail) { Steps[^1] = Steps[^1] with { Mail = mail }; return this; }
}
