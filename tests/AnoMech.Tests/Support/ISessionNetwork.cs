using AnoMech.Multiplayer;
using AnoMech.Relay;

namespace AnoMech.Tests;

// Where a session's TestClients reach their relay (TestRelay, or LiveRelay over a real connection):
// the connections they make, the clock they all run on, and what each one's link has to tell it,
// told within that client's own frame.
internal interface ISessionNetwork
{
    // What the clients host and join at.
    string Address { get; }
    IRelayConnection Connect(string secret);
    TimeProvider Time { get; }
    // Seconds since the session began.
    double Now { get; }
    void AdvanceTo(double time);
    void RaiseLinkEvents(Guid identity);
    // The longest a host's message took from its send to a peer's queue.
    double MaxHostDelay { get; }
    // What the clients refused as they read it, and what the relay refused of what they sent: the
    // relay cuts that link, which reconnects and resends, so nothing else would show it.
    IReadOnlyList<string> Rejected { get; }

    static IEnumerable<string> RefusedBy(RelayServer relay)
    {
        var counts = relay.GetStats().Rejections;
        (long Count, string Why)[] refusals =
        [
            (counts.MessageTooLarge, "too large"), (counts.TooManyFragments, "in too many fragments"),
            (counts.MessageTimeout, "too slow to arrive"), (counts.MessageRate, "over the message rate"),
            (counts.ByteRate, "over the byte rate"),
        ];
        return refusals.Where(r => r.Count > 0).Select(r => $"{r.Count} at the relay: {r.Why}");
    }
}
