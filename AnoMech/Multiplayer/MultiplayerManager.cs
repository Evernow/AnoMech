using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Umad.P3BlackHole;

namespace AnoMech.Multiplayer;

// Owns the multiplayer session lifecycle and the host<->peer replication loop.
// One host runs the real UMAD P3 Black Hole simulation unmodified (RNG, AI,
// DamageSolver, the whole engine exactly as solo play uses it) with joined
// peers' claimed roles spawned as SimNetworkPuppet instead of AI bots
// (PartyCreator). Peers run zero scenario logic themselves -- they load the
// same cosmetic zone/party, then just apply whatever the host broadcasts:
//   - WorldSnapshot (~12Hz): enemy/tether/role poses and casts, replayed
//     through the same public SimWorld/SimEnemy APIs scenarios use, so a
//     peer's local doppels get the real cast-bar/omen/tether VFX pipeline
//     rather than a hand-rolled visual.
//   - RoleKilled: routed through the same Game.Kill every death already
//     funnels through, targeting whatever locally occupies that role (the
//     peer's own real SimPlayer, or that role's local puppet).
// Peers report their own real position back at ~15Hz (SelfPose) so the host's
// puppet for that peer stays where DamageSolver's spatial queries expect it.
//
// All engine calls in this class assume the framework thread (Game.Tick,
// World.SpawnEnemy, SetPosition, etc. are not thread-safe) -- Tick() runs
// there because Plugin drives it from OnFrameworkUpdate, and every handler
// reached from RelayClient.MessageReceived (a background receive thread) is
// marshalled onto it via Plugin.Framework.Run before touching any game state.
public sealed class MultiplayerManager : IDisposable
{
    private const float SnapshotIntervalSeconds = 1f / 12f;
    private const float PoseIntervalSeconds = 1f / 15f;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

    private RelayClient? relay;
    private bool running;
    private float snapshotTimer;
    private float poseTimer;

    private readonly Dictionary<SimEnemy, int> hostEnemyNetIds = new();
    private int nextEnemyNetId;
    private readonly Dictionary<SimTether, int> hostTetherNetIds = new();
    private int nextTetherNetId;

    private readonly Dictionary<int, SimEnemy> peerEnemies = new();
    private readonly Dictionary<int, SimTether> peerTethers = new();
    // RunScenarioAsPeer defers the actual zone load (World.Map.TryLoad, which
    // flips IsInInstance true) onto a Framework.Run callback that hasn't
    // necessarily executed yet by the time OnStartReceived sets running=true --
    // Tick() can observe IsInInstance still false on the very first tick after
    // start and would otherwise immediately kill the peer run before it ever
    // sent a single SelfPose. Only treat IsInInstance==false as "left" once
    // we've actually seen it flip true at least once.
    private bool peerEnteredInstance;

    // ---- Connection-quality tracking ---------------------------------------
    // Host-only ground truth: last time (wall clock) each claimed peer was
    // heard from -- any message, most commonly SelfPose/Pong -- and the last
    // measured round-trip time from a Ping/Pong exchange. Wall-clock
    // (Environment.TickCount64) rather than the scenario-relative float clock
    // this used to key off of, because this now runs continuously (lobby and
    // mid-fight alike), not just while a scenario is running. Runs independent
    // of `running` and of IsHost's toggling since JoinSession/HostSession
    // always start these fresh via LeaveSession.
    private const float PingIntervalSeconds = 2f;
    private const long PeerStaleTimeoutMs = 8000;
    private float pingTimer;
    private readonly Dictionary<Guid, long> peerLastSeenMs = new();
    private readonly Dictionary<Guid, float> peerLatencyMs = new();
    private readonly HashSet<Guid> warnedStalePeers = new();
    // Display-ready status per claimed peer. Host rebuilds this from
    // peerLastSeenMs/peerLatencyMs every ping cycle and broadcasts it
    // (PeerStatusMessage) so peers can render the same roster status without
    // their own liveness bookkeeping; a peer just mirrors whatever it last
    // received here. This is why MultiplayerWindow's status lookup doesn't
    // need to branch on IsHost at all.
    private readonly Dictionary<Guid, PeerStatusEntry> peerStatuses = new();
    // Peer-only: the host is deliberately excluded from peerStatuses above (it
    // never pings itself, so there's no latency number to report) -- without
    // this, a peer's own view of the host's row would be permanently stuck on
    // "waiting for a status update" since it would never receive an entry for
    // the host's own PeerId. Tracked separately from peerLastSeenMs (which is
    // host-only ground truth about *its* peers) by watching every host-
    // originated broadcast a peer receives -- see DispatchCore.
    private long lastHostMessageMs;

    // ---- Reconnection --------------------------------------------------------
    // The relay has no session persistence beyond "sockets currently in the
    // room" -- a dropped socket just falls out of Program.cs's in-memory peer
    // list. Reconnecting with the same session code re-adds a fresh socket to
    // the same room; our identity survives via Configuration.LocalPeerId (a
    // stable per-install Guid, not a fresh one per connection) and the host
    // never auto-releases a claimed role on staleness, so rejoining resumes
    // right where things left off (HelloMessage's BroadcastLobbyState +
    // OnStartReceived's existing late-join handling do the rest).
    private static readonly TimeSpan[] ReconnectBackoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? reconnectCts;
    private bool reconnecting;
    public bool IsReconnecting => reconnecting;
    public int ReconnectAttempt { get; private set; }

    public MultiplayerSession Session { get; private set; } = new();
    public Guid MyPeerId { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsConnected => relay?.IsConnected ?? false;
    public bool IsRunning => running;
    public string? SessionCode { get; private set; }
    public string? RelayUrl { get; private set; }
    public string DisplayName { get; set; } = "Player";
    // Set from RelayClient.Disconnected when the failure came from a *failed
    // connect* (bad ws/wss scheme, relay unreachable, TLS misconfig) rather
    // than a later drop -- otherwise those failures were only visible as an
    // "Unobserved exception in Task" in the raw Dalamud log. Cleared whenever
    // a fresh Host/Join attempt starts.
    public string? ConnectionError { get; private set; }
    // Set when a session ends out from under a peer -- the host explicitly
    // left, or we lost contact with them -- rather than via our own "Leave
    // session" click, so the connect screen can say why instead of the roster
    // just silently vanishing. LeaveSession clears it like ConnectionError;
    // the two code paths that actually need it to survive into the connect
    // screen set it right after their own LeaveSession call returns.
    public string? SessionEndReason { get; private set; }

    public PartyRole? MyClaimedRole => Session.RoleOf(MyPeerId);

    public event Action? LobbyChanged;

    // Connection-quality snapshot for a claimed peer, as last measured/relayed
    // by the host. Same accessor for host and peer callers -- see peerStatuses.
    public PeerStatusEntry? GetPeerStatus(Guid peerId) => peerStatuses.GetValueOrDefault(peerId);

    // Peer-only equivalents of IsPeerStale/GetPeerStatus for the host's own
    // roster row -- see lastHostMessageMs.
    public float SecondsSinceHostMessage => (Environment.TickCount64 - lastHostMessageMs) / 1000f;
    public bool IsHostStale => !IsHost && SecondsSinceHostMessage * 1000f > PeerStaleTimeoutMs;

    // ---- Session lifecycle ----------------------------------------------

    public void HostSession(string relayUrl)
    {
        LeaveSession();
        ConnectionError = null;
        MyPeerId = Plugin.Config.LocalPeerId;
        IsHost = true;
        RelayUrl = relayUrl;
        SessionCode = GenerateCode();
        Session = new MultiplayerSession { HostId = MyPeerId };
        Session.Names[MyPeerId] = DisplayName;
        Session.Builds[MyPeerId] = new PeerBuildInfo(PluginBuildInfo.Version, PluginBuildInfo.Checksum);

        relay = new RelayClient();
        WireRelay(relay);
        _ = relay.ConnectAsync(relayUrl, SessionCode);
        LobbyChanged?.Invoke();
    }

    public void JoinSession(string relayUrl, string code)
    {
        LeaveSession();
        ConnectionError = null;
        MyPeerId = Plugin.Config.LocalPeerId;
        IsHost = false;
        RelayUrl = relayUrl;
        SessionCode = code.Trim().ToUpperInvariant();
        Session = new MultiplayerSession();
        // Seed to "now" rather than the long default (0) -- otherwise the host's
        // row would read as having gone silent for decades until the very first
        // host broadcast arrives.
        lastHostMessageMs = Environment.TickCount64;

        relay = new RelayClient();
        WireRelay(relay);
        _ = ConnectAndHelloAsync(relayUrl, SessionCode);
    }

    private async Task ConnectAndHelloAsync(string relayUrl, string code)
    {
        await relay!.ConnectAsync(relayUrl, code);
        await relay.SendAsync(new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum));
    }

    // Wires a freshly-constructed client's events -- shared by HostSession,
    // JoinSession, and the reconnect loop below. Disconnected captures the
    // specific instance so a stale event from an already-replaced/disposed
    // client (see OnDisconnectedOffThread) can be told apart from one about
    // the currently active connection.
    private void WireRelay(RelayClient client)
    {
        client.MessageReceived += OnMessageReceivedOffThread;
        client.Disconnected += failure => OnDisconnectedOffThread(client, failure);
    }

    // Public entry point for a user-initiated leave (button click) -- notifies
    // everyone else, who then tear down via LeaveSessionInternal(false) from
    // Dispatch's SessionEndedMessage case (see there for why anyone leaving
    // ends it for the whole group, not just themselves).
    public void LeaveSession() => LeaveSessionInternal(notifyOthers: true);

    private void LeaveSessionInternal(bool notifyOthers)
    {
        reconnectCts?.Cancel();
        reconnectCts?.Dispose();
        reconnectCts = null;
        reconnecting = false;
        ReconnectAttempt = 0;
        if (IsHost) Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;

        // Peers need to find out someone left -- otherwise they're stuck
        // sitting in a lobby (or a stale zone, if mid-fight) waiting on a
        // session that's already over (see SessionEndedMessage). Dispose() is
        // synchronous and would otherwise usually abort the send before it
        // reaches the wire, so defer disposal until the send actually
        // completes instead of doing it inline below. notifyOthers is false
        // when we're already reacting to someone *else's* SessionEndedMessage
        // -- otherwise every recipient would re-broadcast its own, cascading
        // once per remaining peer for no reason (everyone's leaving anyway).
        if (notifyOthers && relay is { IsConnected: true } activeRelay)
            _ = activeRelay.SendAsync(new SessionEndedMessage(MyPeerId)).ContinueWith(_ => activeRelay.Dispose());
        else
            relay?.Dispose();
        relay = null;

        running = false;
        SessionCode = null;
        RelayUrl = null;
        ConnectionError = null;
        SessionEndReason = null;
        Session = new MultiplayerSession();
        hostEnemyNetIds.Clear();
        hostTetherNetIds.Clear();
        peerEnemies.Clear();
        peerTethers.Clear();
        peerLastSeenMs.Clear();
        peerLatencyMs.Clear();
        peerStatuses.Clear();
        warnedStalePeers.Clear();
        pingTimer = 0f;
    }

    public void Dispose() => LeaveSession();

    // ---- Reconnection ---------------------------------------------------

    // Fired (via OnDisconnectedOffThread) whenever the active relay connection
    // dies unexpectedly rather than through LeaveSession -- a network blip, the
    // relay restarting, a laptop sleeping. Retries with capped backoff until it
    // succeeds or LeaveSession cancels it; there's no attempt limit since the
    // user always has "Leave session" as an escape hatch, and an idle retry
    // every <=15s costs nothing.
    private void BeginReconnect()
    {
        if (SessionCode == null || RelayUrl == null || reconnecting) return;
        reconnecting = true;
        ReconnectAttempt = 0;
        reconnectCts = new CancellationTokenSource();
        _ = ReconnectLoopAsync(RelayUrl, SessionCode, reconnectCts.Token);
    }

    private async Task ReconnectLoopAsync(string relayUrl, string sessionCode, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = ReconnectBackoff[Math.Min(ReconnectAttempt, ReconnectBackoff.Length - 1)];
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            var client = new RelayClient();
            WireRelay(client);
            await client.ConnectAsync(relayUrl, sessionCode).ConfigureAwait(false);
            var connected = client.IsConnected;

            _ = Plugin.Framework.Run(() => FinishReconnectAttempt(client, connected, token));
            if (connected) return;
            ReconnectAttempt++;
        }
    }

    // Runs on the framework thread. `connected` was read synchronously right
    // after ConnectAsync returned, so it needs no further marshalling; only
    // installing the result into game-visible state does.
    private void FinishReconnectAttempt(RelayClient client, bool connected, CancellationToken token)
    {
        if (token.IsCancellationRequested || !connected)
        {
            client.Dispose();
            return;
        }
        relay = client;
        reconnecting = false;
        ReconnectAttempt = 0;
        lastHostMessageMs = Environment.TickCount64; // same "don't read as decades-stale" reasoning as JoinSession
        ConnectionError = null;
        // Re-registers us with the host (refreshes Names, triggers a
        // BroadcastLobbyState) -- our role claim itself is untouched since the
        // host never releases one just for going stale, and the existing
        // LobbyStateMessage handler already resumes us into a running
        // scenario on Started=true, exactly like a first-time late join.
        if (!IsHost) _ = relay.SendAsync(new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum));
        LobbyChanged?.Invoke();
    }

    private static string GenerateCode()
    {
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[rng.Next(CodeAlphabet.Length)]).ToArray());
    }

    // ---- Role claiming ----------------------------------------------------

    public void ClaimRole(PartyRole role)
    {
        if (relay == null) return;
        if (IsHost) ApplyClaim(MyPeerId, role);
        else _ = relay.SendAsync(new ClaimRoleMessage(MyPeerId, role));
    }

    public void ReleaseRole()
    {
        if (relay == null) return;
        if (IsHost) ApplyRelease(MyPeerId);
        else _ = relay.SendAsync(new ReleaseRoleMessage(MyPeerId));
    }

    // Peer-only: MainWindow's plain Reset button routes here instead of
    // calling Game.Reset() directly while connected as a peer, so a reset
    // reaches the whole group (see ResetRequestMessage) instead of only
    // clearing the requester's own local view. The host's own Reset click
    // needs no equivalent -- it's already authoritative and already
    // propagates via the existing Tick()/EndMessage path.
    public void RequestReset()
    {
        if (IsHost || relay is not { IsConnected: true }) return;
        _ = relay.SendAsync(new ResetRequestMessage(MyPeerId));
    }

    private void ApplyClaim(Guid peerId, PartyRole role)
    {
        if (Session.ClaimedBy.TryGetValue(role, out var holder) && holder != peerId) return; // already taken
        // A build mismatch means differing scenario/protocol logic between host
        // and this peer -- letting them into a slot is exactly how the desyncs
        // this project has repeatedly chased happen. Silently rejecting (rather
        // than kicking) is deliberate: it's recoverable the moment they update,
        // no reconnect needed, and IsVersionMismatched is also surfaced in the
        // UI so it isn't a mystery why nothing happened.
        if (IsVersionMismatched(peerId))
        {
            Plugin.Log.Warning($"[Multiplayer] Rejected role claim from {Session.NameOf(peerId)} -- plugin build mismatch.");
            return;
        }
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.ClaimedBy[role] = peerId;
        BroadcastLobbyState();
    }

    // True when peerId's declared build checksum differs from ours. "unknown"
    // (checksumming failed locally, e.g. a locked/unreadable file) never
    // counts as a mismatch either direction -- better to fail open than to
    // block a session over a checksum we couldn't even compute.
    public bool IsVersionMismatched(Guid peerId)
    {
        if (!Session.Builds.TryGetValue(peerId, out var build)) return false;
        if (build.Checksum == "unknown" || PluginBuildInfo.Checksum == "unknown") return false;
        return build.Checksum != PluginBuildInfo.Checksum;
    }

    private void ApplyRelease(Guid peerId)
    {
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        BroadcastLobbyState();
    }

    private void BroadcastLobbyState()
    {
        LobbyChanged?.Invoke();
        _ = relay?.SendAsync(Session.ToMessage());
    }

    // ---- Starting the scenario ---------------------------------------------

    // Host only. myRole must already be claimed (see MyClaimedRole) -- there is
    // no spectator mode: the engine always seats "this client's real character"
    // into a party slot (PartyCreator.Populate), so the host plays too.
    public void StartScenario()
    {
        if (!IsHost || relay == null) return;
        if (!IsConnected)
        {
            Plugin.Log.Warning("[Multiplayer] Cannot start: not connected to the relay.");
            return;
        }
        if (MyClaimedRole is not { } myRole)
        {
            Plugin.Log.Warning("[Multiplayer] Cannot start: host has not claimed a role.");
            return;
        }
        // Belt-and-suspenders on top of ApplyClaim's own rejection: closes the
        // narrow window where a peer's ClaimRoleMessage could theoretically
        // race ahead of their Hello (see IsVersionMismatched).
        if (Session.ClaimedBy.Values.Any(IsVersionMismatched))
        {
            Plugin.Log.Warning("[Multiplayer] Cannot start: one or more claimed players are on a different plugin build.");
            return;
        }

        var scenario = Plugin.GameInstance.Scenarios.OfType<UmadP3BlackHoleScenario>().First();
        var networkRoles = Session.ClaimedBy.Where(kv => kv.Value != MyPeerId).Select(kv => kv.Key).ToHashSet();

        Session.Started = true;
        _ = relay.SendAsync(Session.ToMessage());
        _ = relay.SendAsync(new StartMessage());

        hostEnemyNetIds.Clear();
        hostTetherNetIds.Clear();
        nextEnemyNetId = 0;
        nextTetherNetId = 0;
        warnedStalePeers.Clear();
        var nowMs = Environment.TickCount64;
        foreach (var peerId in Session.ClaimedBy.Values)
            if (peerId != MyPeerId)
                peerLastSeenMs[peerId] = nowMs;
        Plugin.GameInstance.PartyMemberKilled += OnPartyMemberKilledHost;
        Plugin.GameInstance.RunScenarioAsHost(scenario, myRole, selectedAi: 0, selectedWaymark: 0, networkRoles);
        running = true;
        LobbyChanged?.Invoke();
    }

    private void OnStartReceived()
    {
        // Idempotent: a genuine fresh start delivers both a LobbyStateMessage
        // (Started=true) and a StartMessage in quick succession, and the
        // LobbyStateMessage handler below also calls this directly for a late
        // join/reconnect -- without this guard a normal start would trigger
        // RunScenarioAsPeer twice.
        if (IsHost || running) return;
        if (MyClaimedRole is not { } myRole)
        {
            Plugin.Log.Warning("[Multiplayer] Host started the scenario, but I never claimed a role -- ignoring.");
            return;
        }

        var scenario = Plugin.GameInstance.Scenarios.OfType<UmadP3BlackHoleScenario>().First();
        var networkRoles = Enum.GetValues<PartyRole>().Where(r => r != myRole).ToHashSet();

        peerEnemies.Clear();
        peerTethers.Clear();
        peerEnteredInstance = false;
        Plugin.GameInstance.RunScenarioAsPeer(scenario, myRole, selectedWaymark: 0, networkRoles);
        running = true;
    }

    // ---- Per-frame tick (framework thread; see Plugin.OnFrameworkUpdate) ----

    public void Tick(float deltaSeconds)
    {
        if (relay is not { IsConnected: true }) return;

        // Connection-quality tracking runs continuously -- lobby and mid-fight
        // alike -- so the roster's status indicators are already live before
        // anyone clicks Start, not just once running is true below.
        if (IsHost)
        {
            pingTimer += deltaSeconds;
            if (pingTimer >= PingIntervalSeconds)
            {
                pingTimer = 0f;
                SendPingAndRefreshStatuses();
            }
        }
        else if (IsHostStale)
        {
            // A clean "Leave session" click reaches peers via SessionEndedMessage
            // well within PeerStaleTimeoutMs, so this only fires for a host that
            // vanished without warning -- a crash, alt-F4, or hard network drop,
            // where no goodbye message was ever possible. Same end state either
            // way: nobody's left to resume the fight with, so leave the zone (if
            // mid-fight) and the session both, rather than sitting on a frozen
            // roster or a stale zone forever.
            if (running) Plugin.GameInstance.Leave();
            LeaveSession();
            SessionEndReason = "Lost contact with the host.";
            LobbyChanged?.Invoke();
            return;
        }

        if (!running) return;

        if (IsHost)
        {
            // Reset/Leave clears ActiveScenario -- stop broadcasting once the local
            // run has ended rather than spamming empty snapshots (or, worse, a
            // later unrelated solo run) to peers who are still connected.
            if (Plugin.GameInstance.ActiveScenario == null)
            {
                running = false;
                Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
                // Without this, Session.Started stays true forever once a run ends --
                // the Start button in MultiplayerWindow is gated on !Started, so the
                // lobby would be permanently stuck past its first run with no way to
                // retry short of leaving and re-hosting a brand new session/code.
                Session.Started = false;
                _ = relay?.SendAsync(Session.ToMessage());
                // Reset() leaves World.Map.IsInInstance true (deliberately stays
                // in-zone); only Leave()/a natural finish clears it. Read here,
                // now, before anything else can change it.
                _ = relay?.SendAsync(new EndMessage(ReturnedToInn: !Plugin.GameInstance.World.Map.IsInInstance));
                LobbyChanged?.Invoke();
                return;
            }
            snapshotTimer += deltaSeconds;
            if (snapshotTimer < SnapshotIntervalSeconds) return;
            snapshotTimer = 0f;
            SampleAndBroadcastSnapshot();
        }
        else
        {
            if (Plugin.GameInstance.World.Map.IsInInstance)
            {
                peerEnteredInstance = true;
            }
            else if (peerEnteredInstance)
            {
                running = false;
                return;
            }
            else
            {
                // Zone load queued by RunScenarioAsPeer hasn't run yet -- wait
                // rather than tearing down a run that hasn't truly started.
                return;
            }
            poseTimer += deltaSeconds;
            if (poseTimer < PoseIntervalSeconds) return;
            poseTimer = 0f;
            SendSelfPose();
        }
    }

    // ---- Host: sampling the live simulation --------------------------------

    private void SampleAndBroadcastSnapshot()
    {
        var world = Plugin.GameInstance.World;

        var liveEnemies = world.Children.OfType<SimEnemy>().Where(e => e.IsActive).ToList();
        foreach (var stale in hostEnemyNetIds.Keys.Where(e => !liveEnemies.Contains(e)).ToList())
            hostEnemyNetIds.Remove(stale);

        var enemies = new List<EnemyState>(liveEnemies.Count);
        foreach (var enemy in liveEnemies)
        {
            if (!hostEnemyNetIds.TryGetValue(enemy, out var netId))
            {
                netId = nextEnemyNetId++;
                hostEnemyNetIds[enemy] = netId;
            }
            var cfg = enemy.SpawnConfig;
            enemies.Add(new EnemyState(
                netId, enemy.BNpcBaseId, cfg.NameId, cfg.Level, cfg.Targetable, enemy.EnemyListMode,
                cfg.ModelCharaId, cfg.Scale, cfg.HitboxRadius, cfg.InitialModeAttributeFlags, enemy.Visible,
                enemy.Position.X, enemy.Position.Y, enemy.Position.Z, enemy.Rotation,
                enemy.IsCasting, enemy.CastActionId));
        }

        var liveTethers = world.Children.OfType<SimTether>().Where(t => t.IsActive).ToList();
        foreach (var stale in hostTetherNetIds.Keys.Where(t => !liveTethers.Contains(t)).ToList())
            hostTetherNetIds.Remove(stale);

        var tethers = new List<TetherState>(liveTethers.Count);
        foreach (var tether in liveTethers)
        {
            if (!hostTetherNetIds.TryGetValue(tether, out var netId))
            {
                netId = nextTetherNetId++;
                hostTetherNetIds[tether] = netId;
            }
            var (aEnemy, aRole) = ResolveEnd(world, tether.A);
            var (bEnemy, bRole) = ResolveEnd(world, tether.B);
            tethers.Add(new TetherState(netId, tether.TetherId, aEnemy, aRole, bEnemy, bRole));
        }

        var roles = new List<RoleState>(8);
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            var member = world.Party.Get(role);
            var dead = member is ISimPartyMember { Dead: true };
            roles.Add(new RoleState(role, member != null, dead,
                member?.Position.X ?? 0f, member?.Position.Y ?? 0f, member?.Position.Z ?? 0f, member?.Rotation ?? 0f));
        }

        _ = relay!.SendAsync(new WorldSnapshotMessage(enemies, tethers, roles));
    }

    private (int? enemyNetId, PartyRole? role) ResolveEnd(SimWorld world, SimCharacter? c)
    {
        if (c is null) return (null, null);
        if (c is SimEnemy e) return hostEnemyNetIds.TryGetValue(e, out var id) ? (id, null) : (null, null);
        foreach (var role in Enum.GetValues<PartyRole>())
            if (ReferenceEquals(world.Party.Get(role), c)) return (null, role);
        return (null, null);
    }

    private void OnPartyMemberKilledHost(PartyRole role, string cause)
        => _ = relay?.SendAsync(new RoleKilledMessage(role, cause));

    // True once a claimed peer hasn't been heard from for PeerStaleTimeoutMs.
    // Host reads its own ground-truth peerLastSeenMs; a peer reads the same
    // number as last relayed by the host (PeerStatusMessage) -- either way
    // this surfaces in MultiplayerWindow so a dropped connection shows as
    // something other than "their puppet just stopped moving."
    public bool IsPeerStale(Guid peerId) => IsHost
        ? peerLastSeenMs.TryGetValue(peerId, out var lastSeen) && Environment.TickCount64 - lastSeen > PeerStaleTimeoutMs
        : peerStatuses.TryGetValue(peerId, out var entry) && entry.SecondsSinceLastSeen * 1000f > PeerStaleTimeoutMs;

    // Host-only, every PingIntervalSeconds regardless of running: pings every
    // claimed peer, rebuilds the display-ready status snapshot from whatever
    // was last measured (a full cycle behind the very latest Pong, which is
    // fine for a coarse indicator), and broadcasts it so peers' rosters match.
    private void SendPingAndRefreshStatuses()
    {
        var nowMs = Environment.TickCount64;
        _ = relay!.SendAsync(new PingMessage(nowMs));

        peerStatuses.Clear();
        foreach (var peerId in Session.ClaimedBy.Values.Distinct())
        {
            if (peerId == MyPeerId || !peerLastSeenMs.TryGetValue(peerId, out var lastSeen)) continue;
            var latency = peerLatencyMs.TryGetValue(peerId, out var ms) ? ms : (float?)null;
            peerStatuses[peerId] = new PeerStatusEntry(latency, (nowMs - lastSeen) / 1000f);
        }
        _ = relay.SendAsync(new PeerStatusMessage(new Dictionary<Guid, PeerStatusEntry>(peerStatuses)));

        CheckPeerLiveness();
    }

    private void CheckPeerLiveness()
    {
        foreach (var (role, peerId) in Session.ClaimedBy)
        {
            if (peerId == MyPeerId) continue;
            var stale = IsPeerStale(peerId);
            if (stale && warnedStalePeers.Add(peerId))
                Plugin.Log.Warning($"[Multiplayer] {Session.NameOf(peerId)} ({role}) hasn't reported in over {PeerStaleTimeoutMs / 1000}s -- likely disconnected.");
            else if (!stale)
                warnedStalePeers.Remove(peerId);
        }
    }

    // ---- Peer: reporting our own pose --------------------------------------

    private void SendSelfPose()
    {
        var player = Plugin.GameInstance.World.Party.Player;
        if (player == null) return;
        _ = relay!.SendAsync(new SelfPoseMessage(MyPeerId, player.Position.X, player.Position.Y, player.Position.Z, player.Rotation));
    }

    // ---- Host: applying a peer's reported pose to their puppet -------------

    private void OnSelfPoseReceived(SelfPoseMessage msg)
    {
        if (!IsHost) return;
        if (Session.RoleOf(msg.PeerId) is not { } role) return;
        if (Plugin.GameInstance.World.Party.Get(role) is SimNetworkPuppet puppet)
            puppet.ApplyNetworkPose(new Vector3(msg.X, msg.Y, msg.Z), msg.Rotation);
    }

    // ---- Peer: applying a world snapshot ------------------------------------

    private void OnWorldSnapshotReceived(WorldSnapshotMessage snap)
    {
        if (IsHost) return;
        var world = Plugin.GameInstance.World;

        var seenEnemyIds = new HashSet<int>();
        foreach (var e in snap.Enemies)
        {
            seenEnemyIds.Add(e.NetId);
            if (!peerEnemies.TryGetValue(e.NetId, out var enemy))
            {
                var config = new EnemySpawnConfig(
                    e.BNpcBaseId, e.NameId, e.Level, e.Targetable, e.EnemyList, e.Visible,
                    new Placement(new Vector3(e.X, e.Y, e.Z), e.Rotation),
                    e.ModelCharaId, e.Scale, e.HitboxRadius, e.InitialModeAttributeFlags);
                enemy = world.SpawnEnemy(config);
                if (enemy == null) continue;
                peerEnemies[e.NetId] = enemy;
            }
            enemy.SetPosition(new Placement(new Vector3(e.X, e.Y, e.Z), e.Rotation));
            enemy.SetVisible(e.Visible);
            // Rising-edge trigger: reuses the real SimCast pipeline (cast bar +
            // omen VFX) rather than faking either, so timing/placement come from
            // the same code path solo play already exercises.
            if (e.IsCasting && !enemy.IsCasting)
                enemy.Cast(e.CastActionId);
        }
        foreach (var staleId in peerEnemies.Keys.Where(id => !seenEnemyIds.Contains(id)).ToList())
        {
            peerEnemies[staleId].Despawn();
            peerEnemies.Remove(staleId);
        }

        var seenTetherIds = new HashSet<int>();
        foreach (var t in snap.Tethers)
        {
            seenTetherIds.Add(t.NetId);
            if (peerTethers.ContainsKey(t.NetId)) continue;
            var a = ResolvePeerEnd(world, t.AEnemyNetId, t.ARole);
            var b = ResolvePeerEnd(world, t.BEnemyNetId, t.BRole);
            if (a == null && b == null) continue;
            peerTethers[t.NetId] = world.Tether(a, b, t.TetherId);
        }
        foreach (var staleId in peerTethers.Keys.Where(id => !seenTetherIds.Contains(id)).ToList())
        {
            peerTethers[staleId].Despawn();
            peerTethers.Remove(staleId);
        }

        var myRole = MyClaimedRole;
        foreach (var r in snap.Roles)
        {
            if (r.Role == myRole) continue; // our own real SimPlayer -- never network-driven
            if (world.Party.Get(r.Role) is SimNetworkPuppet puppet)
                puppet.ApplyNetworkPose(new Vector3(r.X, r.Y, r.Z), r.Rotation);
        }
    }

    private SimCharacter? ResolvePeerEnd(SimWorld world, int? enemyNetId, PartyRole? role)
    {
        if (enemyNetId is { } id) return peerEnemies.GetValueOrDefault(id);
        if (role is { } r) return world.Party.Get(r);
        return null;
    }

    private void OnRoleKilledReceived(RoleKilledMessage msg)
    {
        if (IsHost) return;
        if (Plugin.GameInstance.World.Party.Get(msg.Role) is ISimPartyMember member)
            Plugin.GameInstance.Kill(member, msg.Cause);
    }

    private void OnEndReceived(EndMessage msg)
    {
        if (IsHost || !running) return;
        running = false;
        // Mirror whichever the host actually did -- Leave() if they left the
        // zone entirely, or Reset() to match them staying in-zone (ready for a
        // quick re-Start) instead of always hard-kicking to the inn.
        if (msg.ReturnedToInn)
            Plugin.GameInstance.Leave();
        else
            Plugin.GameInstance.Reset();
    }

    // ---- Message pump -------------------------------------------------------

    private void OnMessageReceivedOffThread(MpMessage message)
        => Plugin.Framework.Run(() => Dispatch(message));

    // `source` is the specific RelayClient instance this event came from --
    // compared against the current `relay` field so a stale event from a
    // client we've already torn down (LeaveSession) or superseded (a
    // newer reconnect attempt winning the race) is ignored rather than
    // reprocessed or double-triggering another reconnect loop. This also
    // doubles as the "was this intentional" check: LeaveSession always nulls
    // `relay` before the corresponding Disconnected event can be dispatched
    // (Dispose's cancellation unwinds on a background thread and this handler
    // is itself marshalled to run on a later framework tick), so a manual
    // Leave's own Disconnected(null) never reaches past this guard.
    private void OnDisconnectedOffThread(RelayClient source, Exception? failure)
        => Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, source)) return;
            source.Dispose();
            relay = null;
            if (failure != null)
            {
                Plugin.Log.Warning($"[Multiplayer] Disconnected: {failure.Message}");
                ConnectionError = failure.Message;
            }
            LobbyChanged?.Invoke();
            BeginReconnect();
        });

    private void Dispatch(MpMessage message)
    {
        // A single malformed-but-parseable message (an unexpected null, a role
        // enum out of range, whatever) must not take down the framework tick
        // pump that every other plugin system also shares -- log and move on
        // rather than letting one bad packet cascade.
        try
        {
            DispatchCore(message);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[Multiplayer] Error handling {message.GetType().Name}: {e}");
        }
    }

    private void DispatchCore(MpMessage message)
    {
        // Every message type the host actually broadcasts to everyone (as
        // opposed to a fellow peer's request that the relay's dumb fan-out
        // happens to deliver to us too, e.g. another peer's ClaimRoleMessage --
        // this switch just never matches those on a non-host client). Used to
        // drive the host's own roster-row liveness (see lastHostMessageMs);
        // must be kept in sync with the `when !IsHost` cases below.
        // SessionEndedMessage deliberately excluded -- unlike everything else
        // here it isn't guaranteed to have come from the host (any peer can
        // send it now), and it's moot anyway since receiving it tears the
        // whole session down a few lines later regardless.
        if (!IsHost && message is LobbyStateMessage or StartMessage or WorldSnapshotMessage
            or RoleKilledMessage or EndMessage or PingMessage or PeerStatusMessage)
            lastHostMessageMs = Environment.TickCount64;

        switch (message)
        {
            // Host-authoritative: only the host acts on requests other clients send.
            case HelloMessage hello when IsHost:
                peerLastSeenMs[hello.PeerId] = Environment.TickCount64;
                Session.Names[hello.PeerId] = hello.DisplayName;
                Session.Builds[hello.PeerId] = new PeerBuildInfo(hello.Version, hello.Checksum);
                BroadcastLobbyState();
                break;
            case ClaimRoleMessage claim when IsHost:
                peerLastSeenMs[claim.PeerId] = Environment.TickCount64;
                ApplyClaim(claim.PeerId, claim.Role);
                break;
            case ReleaseRoleMessage release when IsHost:
                peerLastSeenMs[release.PeerId] = Environment.TickCount64;
                ApplyRelease(release.PeerId);
                break;
            case SelfPoseMessage pose when IsHost:
                peerLastSeenMs[pose.PeerId] = Environment.TickCount64;
                OnSelfPoseReceived(pose);
                break;
            case PongMessage pong when IsHost:
                peerLastSeenMs[pong.PeerId] = Environment.TickCount64;
                peerLatencyMs[pong.PeerId] = Environment.TickCount64 - pong.SentAtMs;
                break;

            // Peer-facing broadcasts from the host.
            case LobbyStateMessage lobby when !IsHost:
                Session.ApplyLobbyState(lobby);
                LobbyChanged?.Invoke();
                // The host never re-sends Start to an already-open connection --
                // only a fresh StartMessage triggers OnStartReceived. Without this,
                // a peer who connects after Start already fired (a late join, or a
                // manual rejoin after their connection dropped mid-fight) would sit
                // forever on "Connected -- waiting for the host to start" even
                // though the fight is already running. OnStartReceived is itself
                // idempotent, so this is safe to also fall through for it on a
                // normal fresh start (arrives just before StartMessage does).
                if (lobby.Started && MyClaimedRole != null)
                    OnStartReceived();
                break;
            case StartMessage when !IsHost:
                OnStartReceived();
                break;
            case WorldSnapshotMessage snap when !IsHost:
                OnWorldSnapshotReceived(snap);
                break;
            case RoleKilledMessage killed when !IsHost:
                OnRoleKilledReceived(killed);
                break;
            case EndMessage end when !IsHost:
                OnEndReceived(end);
                break;
            case PingMessage ping when !IsHost:
                _ = relay?.SendAsync(new PongMessage(MyPeerId, ping.SentAtMs));
                break;
            case PeerStatusMessage status when !IsHost:
                peerStatuses.Clear();
                foreach (var (id, entry) in status.Statuses)
                    peerStatuses[id] = entry;
                break;
            // No `when !IsHost` guard -- anyone leaving ends the session for
            // the whole group, including the host, so this has to run
            // regardless of role. Read the sender's name before
            // LeaveSessionInternal wipes Session out from under it.
            case SessionEndedMessage ended:
            {
                var who = Session.NameOf(ended.PeerId);
                if (running) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = $"{who} left -- session ended.";
                LobbyChanged?.Invoke();
                break;
            }
            case ResetRequestMessage req when IsHost:
                Plugin.Log.Information($"[Multiplayer] {Session.NameOf(req.PeerId)} requested a reset.");
                Plugin.GameInstance.Reset();
                break;
        }
    }
}
