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
// A message_id counts as "already received" only once its message was saved
// (MessageIdFilter.MarkSaved, right before the ack). So a copy of the same
// message_id
//   * arriving while the first copy is still on its way waits for it and gets the
//     same answer: ack (saved), nack (rejected) or none (held / failed);
//   * arriving after the first copy was saved is acked at once;
//   * arriving after the first copy was held, rejected or never saved (gateway
//     stopped with the row still queued) is processed again as a new message.
// Before, the id counted from arrival, so such a copy could be acked although
// nothing was ever saved (issues #2, #3).
public sealed class AckTracker
{
    private readonly ReplyHub _replies;
    private readonly MessageIdFilter _saved;
    private readonly ConcurrentDictionary<string, Ticket> _inFlight = new(StringComparer.Ordinal);

    public AckTracker(ReplyHub replies, MessageIdFilter saved)
    {
        _replies = replies;
        _saved = saved;
    }

    // A MES line with a message_id arrived. Returns its ticket when it must be
    // processed; null when it is a repeat (already answered, or waiting for the
    // first copy's answer): nothing more to do with this line.
    // key: what makes it the same message (GatewayHost.RepeatKey: machine, data_source
    // and message_id); the message_id alone when not given.
    public Ticket? Begin(string messageId, string sender, string? key = null)
    {
        key ??= messageId;
        while (true)
        {
            var mine = new Ticket(this, key, messageId, sender);
            var ticket = _inFlight.GetOrAdd(key, mine); // one step: a second copy always finds the first
            if (!ReferenceEquals(ticket, mine))
            {
                if (ticket.AddWaiter(sender))
                    return null; // gets the first copy's answer
                continue;        // the first copy just finished: look again
            }

            if (_saved.Contains(key))
            {
                mine.Release(); // saved before: ack again, nothing to process
                return null;
            }
            return mine;
        }
    }

    public sealed class Ticket
    {
        private readonly AckTracker _owner;
        private readonly List<string> _waiters;
        private int _parts = 1; // the hold, released by Release()
        private bool _finished;
        private readonly string _key; // in _inFlight and the saved ids; MessageId goes in the replies

        internal Ticket(AckTracker owner, string key, string messageId, string sender)
        {
            _owner = owner;
            _key = key;
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
            {
                _owner._saved.MarkSaved(_key); // before the ticket goes: a later copy then finds it saved
                Finish(waiter => _owner._replies.Ack(waiter, MessageId));
            }
        }

        // All groups handed over.
        public void Release() => PartDone();

        // Will never be accepted (e.g. no machine_id): tell every waiting sender why.
        public void Reject(string reason) => Finish(waiter => _owner._replies.Nack(waiter, MessageId, reason));

        // Held, or processing failed: no reply. The adapter keeps the message and
        // sends it again; that copy is processed as new.
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

            _owner._inFlight.TryRemove(new KeyValuePair<string, Ticket>(_key, this));
            foreach (var waiter in waiters)
                reply(waiter);
        }
    }
}
