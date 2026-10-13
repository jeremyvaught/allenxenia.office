using AllenBuilding.Application.Assistant;
using AllenBuilding.Domain.Maintenance;

namespace AllenBuilding.Web.Hosting;

// Local, bounded, transient follow-ups. No raw transcript is written to disk/browser storage.
// Production persistent conversations, cleanup policy and durable spend accounting are separate gates.
public sealed record AssistantGuestDraft(Guid Id, Guid Version, AssistantDraft Draft, IReadOnlyList<AssistantSource> Sources,
    DateTimeOffset ExpiresAt, Guid ReviewConversationId, AssistantTenantScope? ImportedScope = null, Guid? ReviewId = null);

public sealed class AssistantConversations : IDisposable
{
    public const int Capacity = 128;
    public const int MaxTurns = 6;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly ITimer _cleanup;

    public AssistantConversations(TimeProvider time)
    {
        _time = time;
        _cleanup = time.CreateTimer(_ => Prune(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    private void Prune()
    {
        lock (_sync)
            foreach (var expired in _entries.Where(x => Expired(x.Value, _time.GetUtcNow())).Select(x => x.Key).ToArray()) _entries.Remove(expired);
    }
    public void Dispose() { _cleanup.Dispose(); lock (_sync) _entries.Clear(); }

    public AssistantConversationContext? ReadContext(Guid id, string owner)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Owner != owner) return null;
            if (Expired(entry, _time.GetUtcNow())) { _entries.Remove(id); return null; }
            return entry.Context;
        }
    }

    public AssistantGuestDraft? ReadGuest(Guid id, string browser)
    {
        lock (_sync) return GuestEntry(id, browser)?.Guest;
    }
    private Entry? GuestEntry(Guid id, string browser)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.Owner != browser + ":guest" || entry.Guest is null) return null;
        if (Expired(entry, _time.GetUtcNow()) || entry.Guest.ExpiresAt <= _time.GetUtcNow()) { _entries.Remove(id); return null; }
        return entry;
    }
    public bool EditGuest(Guid id, Guid version, string browser, AssistantDraft draft, bool cancel)
    {
        lock (_sync)
        {
            var entry = GuestEntry(id, browser);
            if (entry?.Guest is not { } guest || guest.Version != version || entry.Busy || guest.ImportedScope is not null) return false;
            if (cancel) { _entries.Remove(id); return true; }
            if (draft.Kind != AssistantReviewKind.Request || draft.Comment != "") return false;
            var text = WorkOrder.NormalizeRequest(draft.Title, draft.Description, draft.Location);
            entry.Guest = guest with { Version = Guid.NewGuid(), Draft = draft with { Title = text.Title, Description = text.Description, Location = text.Location } };
            return true;
        }
    }
    public GuestTransfer? BeginTransfer(Guid id, Guid version, string browser, AssistantTenantScope scope)
    {
        lock (_sync)
        {
            var entry = GuestEntry(id, browser);
            if (entry?.Guest is not { } guest || guest.Version != version || entry.Busy ||
                (guest.ImportedScope is not null && guest.ImportedScope != scope)) return null;
            // Bind even a failed/uncertain preparation to the original authenticated scope. A retry
            // cannot retarget a possibly committed canonical review or mutate its draft.
            entry.Guest = guest with { ImportedScope = scope }; entry.Busy = true;
            return new(this, id, entry);
        }
    }
    public sealed class GuestTransfer : IDisposable
    {
        private readonly AssistantConversations _store;
        private readonly Guid _id;
        private readonly Entry _entry;
        internal GuestTransfer(AssistantConversations store, Guid id, Entry entry) { _store = store; _id = id; _entry = entry; }
        public AssistantGuestDraft Draft => _entry.Guest!;
        public bool Complete(Guid reviewId)
        {
            lock (_store._sync)
            {
                if (_store.GuestEntry(_id, _entry.Owner[..^6]) != _entry) return false;
                _entry.Guest = Draft with { ReviewId = reviewId }; return true;
            }
        }
        public void Dispose() { lock (_store._sync) _entry.Busy = false; }
    }

    public Lease? Begin(Guid id, int sequence, string owner, Guid? membership, Guid? target)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _entries.Where(x => Expired(x.Value, now)).Select(x => x.Key).ToArray()) _entries.Remove(expired);
            if (sequence < 0) return null;
            if (sequence == 0)
            {
                // The page supplies an opaque ID; it never grants authority. Bind its first admission
                // to this browser, authenticated user and exact selected scope. Reject initial replay.
                if (_entries.Count >= Capacity || _entries.Values.Count(x => x.Owner == owner) >= 8) return null;
                if (id == Guid.Empty) id = Guid.NewGuid();
                if (_entries.ContainsKey(id)) return null;
                _entries.Add(id, new(owner, membership, target, now));
            }
            if (!_entries.TryGetValue(id, out var entry) || entry.Owner != owner || entry.Membership != membership || entry.Target != target ||
                entry.Sequence != sequence || entry.Busy || entry.Closed || entry.Sequence >= MaxTurns) return null;
            entry.Busy = true;
            return new(this, id, entry, entry.Context);
        }
    }

    private static bool Expired(Entry entry, DateTimeOffset now) => (entry.Guest is { } guest && guest.ExpiresAt <= now) || now - entry.LastSeen >= TimeSpan.FromMinutes(30) || now - entry.Created >= TimeSpan.FromHours(2);
    private bool Finish(Lease lease, string question, AssistantAnswer answer)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            if (!_entries.TryGetValue(lease.Id, out var entry) || entry != lease.Entry || !entry.Busy || Expired(entry, now)) return false;
            var history = (entry.Context?.History ?? []).Append(new AssistantExchange(question.Trim(), answer.Text)).ToArray();
            entry.Context = new(answer.Scope, answer.ContextualSources ?? [], history);
            if (answer.GuestDraft is { } draft)
            {
                if (!entry.Owner.EndsWith(":guest", StringComparison.Ordinal) || entry.Membership is not null || entry.Target is not null || answer.Scope is not null) return false;
                entry.Guest = new(lease.Id, Guid.NewGuid(), draft, answer.ContextualSources ?? [], now.AddMinutes(10), Guid.NewGuid());
                // Retain payload/provenance only once prepared, never transfer the raw conversation.
                entry.Context = new(null, answer.ContextualSources ?? [], []);
            }
            entry.Sequence++; entry.LastSeen = now; entry.Busy = false;
            // A prepared write ends language turns; its canonical review has its own durable lifecycle.
            entry.Closed = answer.GuestDraft is not null || answer.Review is not null || entry.Sequence >= MaxTurns || history.Sum(x => x.Question.Length + x.Answer.Length) >= 10000;
            return true;
        }
    }
    private void Abandon(Lease lease, bool forget)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(lease.Id, out var entry) || entry != lease.Entry) return;
            if (forget || entry.Sequence == 0) _entries.Remove(lease.Id);
            else entry.Busy = false;
        }
    }
    internal sealed class Entry(string owner, Guid? membership, Guid? target, DateTimeOffset now)
    {
        public string Owner { get; } = owner;
        public Guid? Membership { get; } = membership;
        public Guid? Target { get; } = target;
        public DateTimeOffset Created { get; } = now;
        public DateTimeOffset LastSeen { get; set; } = now;
        public int Sequence { get; set; }
        public bool Busy { get; set; }
        public bool Closed { get; set; }
        public AssistantConversationContext? Context { get; set; }
        public AssistantGuestDraft? Guest { get; set; }
    }
    public sealed class Lease : IDisposable
    {
        private readonly AssistantConversations _store;
        private bool _finished;
        internal Lease(AssistantConversations store, Guid id, Entry entry, AssistantConversationContext? previous)
        { _store = store; Id = id; Entry = entry; Previous = previous; }
        public Guid Id { get; }
        internal Entry Entry { get; }
        public AssistantConversationContext? Previous { get; }
        public int Sequence => Entry.Sequence;
        public bool Closed => Entry.Closed;
        public bool Complete(string question, AssistantAnswer answer)
        {
            if (_finished || answer.Code != AssistantModelCode.Success) return false;
            _finished = _store.Finish(this, question, answer);
            return _finished;
        }
        public void Forget() { _store.Abandon(this, true); _finished = true; }
        public void Dispose() { if (!_finished) _store.Abandon(this, false); }
    }
}
