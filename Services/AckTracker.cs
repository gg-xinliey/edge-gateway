using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Decides when a MES message gets its {"ack":"<message_id>"} reply: only once
// everything it changed is committed to the outbox file.
//
// One message can change several data groups (one outbox row each) or none
// (same values as before: nothing new to save, so it is answered right away).
// A ticket counts the rows still on their way to disk, plus one "hold" while
// the message is being split into groups, so it cannot finish early.
//
// The same message_id arriving again while the first copy is still on its way
// waits for that ticket, so it is never answered before the data is on disk.
public sealed class AckTracker
{
    private readonly ReplyHub _replies;
    private readonly ConcurrentDictionary<string, Ticket> _inFlight = new(StringComparer.Ordinal);

    public AckTracker(ReplyHub replies) => _replies = replies;

    // Right after the message_id passed the duplicate check.
    public Ticket Begin(string messageId, string sender)
    {
        var ticket = new Ticket(this, messageId, sender);
        _inFlight[messageId] = ticket;
        return ticket;
    }

    // A repeated message_id: answer when the first copy is on disk (now, if it already is).
    public void Repeat(string messageId, string sender)
    {
        if (_inFlight.TryGetValue(messageId, out var ticket) && ticket.AddWaiter(sender))
            return;
        _replies.Ack(sender, messageId);
    }

    public sealed class Ticket
    {
        private readonly AckTracker _owner;
        private readonly List<string> _waiters;
        private int _parts = 1; // the hold, released by Release()
        private bool _finished;

        internal Ticket(AckTracker owner, string messageId, string sender)
        {
            _owner = owner;
            MessageId = messageId;
            _waiters = new List<string> { sender };
        }

        public string MessageId { get; }

        // Before handing a row to the outbox (so its save can never finish the ticket early).
        public void AddPart() => Interlocked.Increment(ref _parts);

        // A row is on disk (or turned out not to be needed).
        public void PartDone()
        {
            if (Interlocked.Decrement(ref _parts) == 0)
                Finish(waiter => _owner._replies.Ack(waiter, MessageId));
        }

        // All groups handed over.
        public void Release() => PartDone();

        // Will never be accepted (e.g. no machine_id): tell every waiting sender why.
        public void Reject(string reason) => Finish(waiter => _owner._replies.Nack(waiter, MessageId, reason));

        // Not a MES message (IO/motion) or processing failed: no reply. The
        // adapter decides (IO/motion are not kept; a failed one is sent again).
        public void Drop() => Finish(_ => { });

        internal bool AddWaiter(string sender)
        {
            lock (_waiters)
            {
                if (_finished)
                    return false;
                _waiters.Add(sender);
                return true;
            }
        }

        private void Finish(Action<string> reply)
        {
            string[] waiters;
            lock (_waiters)
            {
                if (_finished)
                    return;
                _finished = true;
                waiters = _waiters.ToArray();
            }

            _owner._inFlight.TryRemove(new KeyValuePair<string, Ticket>(MessageId, this));
            foreach (var waiter in waiters)
                reply(waiter);
        }
    }
}
