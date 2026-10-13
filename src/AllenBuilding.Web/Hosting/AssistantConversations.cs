using AllenBuilding.Application.Assistant;

namespace AllenBuilding.Web.Hosting;

// Local, bounded, transient follow-ups. No raw transcript is written to disk/browser storage.
// Production persistent conversations, cleanup policy and durable spend accounting are separate gates.
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

    private static bool Expired(Entry entry, DateTimeOffset now) => now - entry.LastSeen >= TimeSpan.FromMinutes(30) || now - entry.Created >= TimeSpan.FromHours(2);
    private bool Finish(Lease lease, string question, AssistantAnswer answer)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            if (!_entries.TryGetValue(lease.Id, out var entry) || entry != lease.Entry || !entry.Busy || Expired(entry, now)) return false;
            var history = (entry.Context?.History ?? []).Append(new AssistantExchange(question.Trim(), answer.Text)).ToArray();
            entry.Context = new(answer.Scope, answer.ContextualSources ?? [], history);
            entry.Sequence++; entry.LastSeen = now; entry.Busy = false;
            // A prepared write ends language turns; its canonical review has its own durable lifecycle.
            entry.Closed = answer.Review is not null || entry.Sequence >= MaxTurns || history.Sum(x => x.Question.Length + x.Answer.Length) >= 10000;
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
