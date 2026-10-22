using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AnoMech.Multiplayer;
using AnoMech.Network;
using AnoMech.Relay;

namespace AnoMech.Tests;

// The real relay listening on this machine's loopback, every client on a real RelayClient over TCP
// as a player's is, in real time. A RelayClient calls back on its own threads, and Plugin's
// services are process-wide, so what MultiplayerManager handles through them (a disconnect, a
// connect finishing) waits for that client's next frame; messages go straight to its queue, as in
// game.
internal sealed class LiveRelay : ISessionNetwork, IDisposable
{
    private readonly RelayServer server = new(new RelayOptions { BindAddress = IPAddress.Loopback, Port = 0 }, new QuietLog());
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Action>> frames = new();
    private readonly ConcurrentBag<RelayClient> clients = [];
    private readonly ConcurrentQueue<string> rejected = new();
    // When each host message was sent, by what it says, oldest first.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> hostSends = new();
    private readonly object delayLock = new();
    private Guid hostIdentity;
    private double maxHostDelay;

    public LiveRelay()
    {
        server.Start();
        Address = $"ws://127.0.0.1:{server.LocalEndPoint!.Port}";
    }

    public string Address { get; }
    public TimeProvider Time => TimeProvider.System;
    public double Now => clock.Elapsed.TotalSeconds;
    public IReadOnlyList<string> Rejected => [.. rejected, .. ISessionNetwork.RefusedBy(server)];

    public double MaxHostDelay
    {
        get { lock (delayLock) return maxHostDelay; }
    }

    public IRelayConnection Connect(string secret)
    {
        var client = new RelayClient(secret);
        clients.Add(client);
        var identity = RelayWire.PeerId(secret);
        return new Link(this, client, identity, frames.GetOrAdd(identity, _ => new ConcurrentQueue<Action>()));
    }

    public void AdvanceTo(double time)
    {
        while (Now < time)
        {
            var milliseconds = (int)((time - Now) * 1000);
            if (milliseconds > 1) Thread.Sleep(milliseconds - 1);
            else Thread.Yield();
        }
    }

    public void RaiseLinkEvents(Guid identity)
    {
        if (!frames.TryGetValue(identity, out var frame)) return;
        while (frame.TryDequeue(out var call)) call();
    }

    public void Dispose()
    {
        foreach (var client in clients) client.Dispose();
        server.StopAsync().GetAwaiter().GetResult();
    }

    private void HostSent(MpMessage message)
    {
        var sends = hostSends.GetOrAdd(JsonSerializer.Serialize(message), _ => new ConcurrentQueue<double>());
        sends.Enqueue(Now);
    }

    private void PeerReceived(MpMessage message)
    {
        if (!hostSends.TryGetValue(JsonSerializer.Serialize(message), out var sends) || !sends.TryDequeue(out var sentAt)) return;
        lock (delayLock) maxHostDelay = Math.Max(maxHostDelay, Now - sentAt);
    }

    private sealed class Link : IRelayConnection
    {
        private readonly LiveRelay relay;
        private readonly RelayClient client;
        private readonly Guid identity;
        private readonly ConcurrentQueue<Action> frame;

        public Link(LiveRelay relay, RelayClient client, Guid identity, ConcurrentQueue<Action> frame)
        {
            this.relay = relay;
            this.client = client;
            this.identity = identity;
            this.frame = frame;
            client.MessageReceived += (message, isFromHost, senderId, sender) =>
            {
                if (isFromHost) relay.PeerReceived(message);
                MessageReceived?.Invoke(message, isFromHost, senderId, sender);
            };
            client.PeersRemoved += removed => PeersRemoved?.Invoke(removed);
            client.MessageRejected += (sender, isFromHost, reason) => relay.rejected.Enqueue($"a message from {sender}: {reason}");
            client.Disconnected += failure => frame.Enqueue(() => Disconnected?.Invoke(failure));
        }

        public bool IsConnected => client.IsConnected;
        public bool IsEncrypted => client.IsEncrypted;
        public bool FellBackToUnencrypted => client.FellBackToUnencrypted;
        public bool SupportsCompression => client.SupportsCompression;
        public bool SupportsSenderIdentity => client.SupportsSenderIdentity;

        public event Action<MpMessage, bool, uint, Guid>? MessageReceived;
        public event Action<IReadOnlyList<Guid>>? PeersRemoved;
        public event Action<Exception?>? Disconnected;

        public Task ConnectAsync(string relayUrl, string sessionCode, string? accessToken = null)
            => InFrame(client.ConnectAsync(relayUrl, sessionCode, accessToken));

        public Task<string?> ConnectAndHostAsync(string relayUrl, string? accessToken = null)
        {
            relay.hostIdentity = identity;
            return InFrame(client.ConnectAndHostAsync(relayUrl, accessToken));
        }

        public Task SendAsync(MpMessage message)
        {
            if (identity == relay.hostIdentity) relay.HostSent(message);
            return client.SendAsync(message);
        }

        public Task ModerateAsync(string operation, Guid peerId) => client.ModerateAsync(operation, peerId);

        public void Dispose() => client.Dispose();

        private Task InFrame(Task task)
        {
            var done = new TaskCompletionSource();
            task.ContinueWith(finished => frame.Enqueue(() =>
            {
                if (finished.Exception is { } failure) done.TrySetException(failure.InnerExceptions);
                else done.TrySetResult();
            }), TaskScheduler.Default);
            return done.Task;
        }

        private Task<T> InFrame<T>(Task<T> task)
        {
            var done = new TaskCompletionSource<T>();
            task.ContinueWith(finished => frame.Enqueue(() =>
            {
                if (finished.Exception is { } failure) done.TrySetException(failure.InnerExceptions);
                else done.TrySetResult(finished.Result);
            }), TaskScheduler.Default);
            return done.Task;
        }
    }
}
