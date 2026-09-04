using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P4KefkaSays;
using AnoMech.Scenarios.Umad.P5Exaflares;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Multiplayer;

// Owns the multiplayer session lifecycle and the host<->peer replication loop. The host
// runs the real scenario unmodified, with joined peers' claimed roles spawned as
// SimNetworkPuppet instead of AI bots. Peers run no scenario logic themselves -- they load
// the same cosmetic zone/party and apply whatever the host broadcasts (WorldSnapshot each
// host Tick; RoleKilled through the normal Game.Kill path), reporting their own real
// position back every frame (SelfPose) so the host's puppet tracks it.
//
// All engine calls here assume the framework thread -- Tick() runs there via
// OnFrameworkUpdate, and every handler reached from RelayClient.MessageReceived (a
// background thread) is marshalled onto it via Plugin.Framework.Run first.
public sealed partial class MultiplayerManager : IDisposable
{
    // Mirrors UmadP5ExaflaresScenario.FrameGapCapSeconds -- guards a peer's P5 debug-bot
    // replay (Tick()'s peer branch) against a pause/loading-stall frame firing every queued
    // timeline event at once.
    private const float P5ReplayFrameGapCapSeconds = 0.25f;

    private RelayClient? relay;
    private bool running;

    private readonly Dictionary<SimEnemy, int> hostEnemyNetIds = new();
    private int nextEnemyNetId;
    private readonly Dictionary<SimTether, int> hostTetherNetIds = new();
    private int nextTetherNetId;
    // Host-only: edge-triggered logging so a mid-fight change gets one log line instead of
    // one per sample. ModelState/statuses/animation are still sent in full every snapshot.
    private readonly Dictionary<SimEnemy, byte> hostEnemyLastLoggedModelState = new();
    // Per-status-id (not just "did the set change") so LogStatusChanges can log an
    // individual gain/loss/stack-change line instead of one "the whole set changed" summary.
    private readonly Dictionary<SimEnemy, Dictionary<ushort, ushort>> hostEnemyLastLoggedStatuses = new();
    private readonly Dictionary<SimEnemy, int> hostEnemyLastLoggedAnimationTimeline = new();
    private readonly Dictionary<PartyRole, Dictionary<ushort, ushort>> hostRoleLastLoggedStatuses = new();
    // Host-only, edge-triggered against UmadP2ForsakenState.Lockons -- see
    // P2LockonsUpdateMessage for why this needs its own re-syncable channel.
    private string? hostLastBroadcastP2Lockons;

    private readonly Dictionary<SimEventObject, int> hostEventObjectNetIds = new();
    private int nextEventObjectNetId;

    private readonly Dictionary<int, SimEnemy> peerEnemies = new();
    private readonly Dictionary<int, SimTether> peerTethers = new();
    private readonly Dictionary<int, SimEventObject> peerEventObjects = new();
    // Peer-only: last-applied value per NetId, so a no-op resend isn't reissued every
    // snapshot -- SetModelState's native rebuild flickers the model, animation replay would
    // restart the loop, etc.
    private readonly Dictionary<int, byte> peerEnemyModelState = new();
    private readonly Dictionary<int, Dictionary<ushort, ushort>> peerEnemyLastLoggedStatuses = new();
    private readonly Dictionary<int, int> peerEnemyAnimationTimeline = new();
    // Peer-only: see EnemyState.LastInstantCastSeq/CastSeq for why instant and telegraphed
    // casts each need their own dedup counter instead of the IsCasting rising edge.
    private readonly Dictionary<int, int> peerEnemyLastInstantCastSeq = new();
    private readonly Dictionary<int, int> peerEnemyLastCastSeq = new();
    private readonly Dictionary<int, ushort> peerEventObjectState = new();
    // Peer-only role equivalent of peerEnemyLastLoggedStatuses -- reconciled/applied for
    // every role including the peer's own, since a peer never runs scenario logic itself.
    private readonly Dictionary<PartyRole, Dictionary<ushort, ushort>> peerRoleLastLoggedStatuses = new();
    // Peer-only: statusIds THIS reconciliation applied to a role, as opposed to one the
    // local client manages itself (e.g. Sprint via LocalPlayerInputHooks on the peer's own
    // claimed role) -- removal here must only ever undo what this code added.
    private readonly Dictionary<PartyRole, HashSet<ushort>> peerRoleReconciledStatusIds = new();
    // RunScenarioAsPeer's zone load is deferred a frame past OnStartReceived setting
    // running=true -- only treat IsInInstance==false as "left" once it's flipped true once.
    private bool peerEnteredInstance;

    // ---- Connection-quality tracking ---------------------------------------
    // Host-only ground truth: last-heard wall-clock time and last measured RTT per claimed
    // peer. Runs continuously (lobby and mid-fight), independent of `running`/IsHost.
    private const float PingIntervalSeconds = 2f;
    private const long PeerStaleTimeoutMs = 8000;
    // Shorter timeout for "never heard from a host at all" -- catches a mistyped/nonexistent
    // code, since the relay has no "session not found" at the transport level.
    private const long NoHostFoundTimeoutMs = 4000;
    private float pingTimer;
    private readonly Dictionary<Guid, long> peerLastSeenMs = new();
    private readonly Dictionary<Guid, float> peerLatencyMs = new();
    private readonly HashSet<Guid> warnedStalePeers = new();
    // Host-only: a peer's own real tank-mitigation statuses, self-reported (see
    // SelfMitigationMessage) since a peer's real button press never reaches the host's
    // SimNetworkPuppet copy of them. Read by TankMitigation.ComputeMitigation.
    private readonly Dictionary<Guid, HashSet<ushort>> peerMitigationStatusIds = new();
    // Display-ready status per claimed peer, rebuilt by the host each ping cycle and
    // broadcast (PeerStatusMessage) so peers can render the roster without their own
    // liveness bookkeeping.
    private readonly Dictionary<Guid, PeerStatusEntry> peerStatuses = new();
    // Peer-only: the host's own roster row, since it's excluded from peerStatuses (it never
    // pings itself). Updated by watching every host-originated broadcast -- see DispatchCore.
    private long lastHostMessageMs;
    // Distinguishes "never heard from a host" from "was hearing, then stopped" --
    // lastHostMessageMs alone can't, since it's seeded to "now" on every join/reconnect.
    private bool everHeardFromHost;

    // ---- Pre-Start readiness check -----------------------------------------
    // Host-only: mid-flight state for a StartScenario call awaiting every claimed peer's
    // StartCheckResponseMessage -- see StartScenario/FinishStartCheck/Tick().
    private const float StartCheckTimeoutSeconds = 5f;
    private HashSet<Guid>? pendingStartResponses;
    private readonly Dictionary<Guid, string> startCheckFailures = new();
    private float startCheckTimer;
    public bool IsStartCheckPending => pendingStartResponses != null;
    public string? StartCheckFailureReason { get; private set; }

    // ---- Debug: bot-controlled host or peer ---------------------------------
    // Testing aid: drives the user's own claimed role via the same AiManager choreography a
    // bot would produce, so one developer can fill a multi-person session alone. For the
    // host, scenario.Run already scheduled that choreography live -- this flag just stops
    // PlayerMovement.MoveTo from no-op'ing it. For a peer it's reconstructed from a
    // broadcast AiReplayStateMessage (see TrySendAiReplayState/TryStartDebugBotReplay).
    // Sticky across Start/Reset within a session; lobby-only to toggle.
    private bool debugBotControlled;
    public bool DebugBotControlled => debugBotControlled;

    // Host-only: whether AiReplayStateMessage already went out this run -- edge-triggered
    // against UmadP3BlackHoleScenario.LastState.
    private bool aiReplayStateSent;

    // Host-only: BroadcastRunEnded's EndMessage is fire-and-forget -- these track resends
    // still owed if it's lost, since a peer otherwise only notices via PeerStaleTimeoutMs.
    private const int EndMessageResendCount = 4;
    private const float EndMessageResendIntervalSeconds = 1f;
    private bool? pendingEndResendReturnedToInn;
    private int endResendsRemaining;
    private float endResendTimer;

    // Host-only: RunScenarioAsHost's real work (setting Game.ActiveScenario) is deferred a
    // frame past `running` being set true -- without this, Tick() can see ActiveScenario
    // still null and wrongly broadcast an end-of-run right after a real start.
    private bool hostScenarioStarted;

    // Peer-only: host's broadcast AI-replay values, buffered until peerEnteredInstance (order
    // vs. the host broadcast isn't guaranteed). Kept around after replay starts so
    // OnWorldSnapshotReceived can keep resolving ScenarioObjects from newly-seen enemies.
    private AiReplayStateMessage? pendingAiReplayState;
    private UmadP3BlackHoleState? debugShadowState;
    // Per-scenario siblings of the two fields above -- only one is ever non-null per run.
    private P2AiReplayStateMessage? pendingP2AiReplayState;
    private UmadP2ForsakenState? debugShadowStateP2;
    private P4AiReplayStateMessage? pendingP4AiReplayState;
    private UmadP4KefkaSaysState? debugShadowStateP4;
    private P5AiReplayStateMessage? pendingP5AiReplayState;
    private UmadP5ExaflaresState? debugShadowStateP5;
    private bool debugBotReplayStarted;

    public bool SetDebugBotControlled(bool value)
    {
        if (running) return false; // toggling mid-fight would be a silent no-op anyway
        debugBotControlled = value;
        return true;
    }

    // ---- Reconnection --------------------------------------------------------
    // The relay has no session persistence beyond currently-open sockets -- reconnecting
    // with the same code re-adds a fresh socket. Identity survives via
    // Configuration.LocalPeerId (stable per-install), and the host never releases a claimed
    // role on staleness, so rejoining resumes where it left off.
    private static readonly TimeSpan[] ReconnectBackoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? reconnectCts;
    private bool reconnecting;
    public bool IsReconnecting => reconnecting;
    // Host-only: when the host's own relay connection went down; null while connected.
    // Without this the host retries forever while the fight runs on locally, oblivious that
    // every peer already gave up via IsHostStale.
    private long? disconnectedSinceMs;
    public int ReconnectAttempt { get; private set; }

    public MultiplayerSession Session { get; private set; } = new();
    public Guid MyPeerId { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsConnected => relay?.IsConnected ?? false;
    public bool IsEncrypted => relay?.IsEncrypted ?? false;
    public bool FellBackToUnencrypted => relay?.FellBackToUnencrypted ?? false;
    public bool SupportsCompression => relay?.SupportsCompression ?? false;
    public bool IsRunning => running;
    public string? SessionCode { get; private set; }
    public string? RelayUrl { get; private set; }
    // Read fresh from config at Host/Join time (Plugin.Config.RelayAccessToken), then reused
    // for the reconnect loop the same way RelayUrl is -- so a mid-session config edit can't
    // change what an already-open reconnect attempt sends.
    private string? relayAccessToken;
    public string DisplayName { get; set; } = "Player";
    // Set when RelayClient.Disconnected fires from a failed connect (bad scheme, relay
    // unreachable, TLS misconfig) rather than a later drop, so it's surfaced in the UI
    // instead of only an unobserved Task exception. Cleared on the next Host/Join attempt.
    public string? ConnectionError { get; private set; }
    // Set when a session ends out from under a peer (host left, or contact lost) rather than
    // via this client's own Leave click, so the connect screen can explain why.
    public string? SessionEndReason { get; private set; }

    public PartyRole? MyClaimedRole => Session.RoleOf(MyPeerId);

    public event Action? LobbyChanged;

    public PeerStatusEntry? GetPeerStatus(Guid peerId) => peerStatuses.GetValueOrDefault(peerId);

    // Host-only: whatever `role`'s claimed peer last self-reported as active (see
    // SelfMitigationMessage) -- empty if unclaimed, unreported yet, or called on a peer
    // client (a peer has no visibility into another peer's statuses).
    public IReadOnlyCollection<ushort> PeerMitigationStatusIds(PartyRole role)
    {
        if (!IsHost || !Session.ClaimedBy.TryGetValue(role, out var peerId)) return [];
        return peerMitigationStatusIds.TryGetValue(peerId, out var ids) ? ids : [];
    }

    public float SecondsSinceHostMessage => (Environment.TickCount64 - lastHostMessageMs) / 1000f;
    // Lets MultiplayerWindow hold a peer on "connecting" instead of the full lobby until a
    // host is actually confirmed present -- SessionCode alone is set synchronously on Join.
    public bool EverHeardFromHost => everHeardFromHost;
    public bool IsHostStale => !IsHost && everHeardFromHost && SecondsSinceHostMessage * 1000f > PeerStaleTimeoutMs;
    public bool IsSessionNotFound => !IsHost && !everHeardFromHost && SecondsSinceHostMessage * 1000f > NoHostFoundTimeoutMs;

    // ---- Session lifecycle ----------------------------------------------

    public void HostSession(string relayUrl)
    {
        LeaveSession();
        ConnectionError = null;
        MyPeerId = Plugin.Config.LocalPeerId;
        IsHost = true;
        RelayUrl = relayUrl;
        relayAccessToken = Plugin.Config.RelayAccessToken;
        Session = new MultiplayerSession { HostId = MyPeerId };
        Session.Names[MyPeerId] = DisplayName;
        Session.Builds[MyPeerId] = new PeerBuildInfo(PluginBuildInfo.Version, PluginBuildInfo.Checksum);

        DiagnosticLog.Info($"[Multiplayer] Hosting a new session at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
        var client = new RelayClient();
        WireRelay(client);
        relay = client;
        _ = FinishHostConnectAsync(client);
        LobbyChanged?.Invoke();
    }

    // `client` is captured rather than reading `relay` after the await, so a
    // LeaveSession/fresh Host/Join that replaces `relay` mid-flight can't resurrect an
    // abandoned session with a late-arriving code (same guard as OnDisconnectedOffThread).
    private async Task FinishHostConnectAsync(RelayClient client)
    {
        var code = await client.ConnectAndHostAsync(RelayUrl!, relayAccessToken);
        if (!ReferenceEquals(relay, client)) return;
        if (code is null) return; // Disconnected already fired from inside ConnectAndHostAsync
        SessionCode = code;
        DiagnosticLog.Info($"[Multiplayer] Relay assigned session code {code}.");
        LobbyChanged?.Invoke();
    }

    public void JoinSession(string relayUrl, string code)
    {
        LeaveSession();
        ConnectionError = null;
        MyPeerId = Plugin.Config.LocalPeerId;
        IsHost = false;
        RelayUrl = relayUrl;
        relayAccessToken = Plugin.Config.RelayAccessToken;
        SessionCode = code.Trim().ToUpperInvariant();
        Session = new MultiplayerSession();
        // Seed to "now", not the long default (0), or the host's row reads as silent for
        // decades until the first broadcast arrives.
        lastHostMessageMs = Environment.TickCount64;
        everHeardFromHost = false;

        DiagnosticLog.Info($"[Multiplayer] Joining session {SessionCode} at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
        relay = new RelayClient();
        WireRelay(relay);
        _ = ConnectAndHelloAsync(relayUrl, SessionCode);
    }

    private async Task ConnectAndHelloAsync(string relayUrl, string code)
    {
        await relay!.ConnectAsync(relayUrl, code, relayAccessToken);
        DiagnosticLog.Info($"[Multiplayer] Connected to relay, socket ready -- sending Hello.");
        await relay.SendAsync(new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum));
    }

    // Shared by HostSession, JoinSession, and the reconnect loop. Disconnected captures the
    // specific instance so a stale event from an already-replaced client can be told apart
    // from one about the currently active connection.
    private void WireRelay(RelayClient client)
    {
        client.MessageReceived += OnMessageReceivedOffThread;
        client.Disconnected += failure => OnDisconnectedOffThread(client, failure);
    }

    public void LeaveSession() => LeaveSessionInternal(notifyOthers: true);

    private void LeaveSessionInternal(bool notifyOthers)
    {
        if (SessionCode != null)
            DiagnosticLog.Info($"[Multiplayer] Leaving session {SessionCode} (was {(IsHost ? "host" : "peer")}, notifyOthers={notifyOthers}).");
        reconnectCts?.Cancel();
        reconnectCts?.Dispose();
        reconnectCts = null;
        reconnecting = false;
        ReconnectAttempt = 0;
        disconnectedSinceMs = null;
        if (IsHost) Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;

        // Notify so peers don't sit stuck waiting on a session that's already over (see
        // SessionEndedMessage). Defer Dispose() until the send completes, or it usually
        // aborts before reaching the wire. notifyOthers is false when reacting to someone
        // else's SessionEndedMessage, to avoid a broadcast cascade.
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
        hostEnemyLastLoggedModelState.Clear();
        hostEnemyLastLoggedStatuses.Clear();
        hostEnemyLastLoggedAnimationTimeline.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        peerEnemies.Clear();
        peerEnemyModelState.Clear();
        peerEnemyLastLoggedStatuses.Clear();
        peerEnemyAnimationTimeline.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerEnemyLastCastSeq.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerLastSeenMs.Clear();
        peerLatencyMs.Clear();
        peerStatuses.Clear();
        peerMitigationStatusIds.Clear();
        lastSentMitigationStatusIds.Clear();
        lastSentShieldFraction = 0f;
        TankShieldTracker.Reset();
        warnedStalePeers.Clear();
        pingTimer = 0f;
        pendingStartResponses = null;
        startCheckFailures.Clear();
        StartCheckFailureReason = null;
        startCheckTimer = 0f;
        debugBotControlled = false;
        aiReplayStateSent = false;
        pendingEndResendReturnedToInn = null;
        StopDebugBotReplay();
    }

    public void Dispose() => LeaveSession();

    // ---- Reconnection ---------------------------------------------------

    // Fired when the active relay connection dies unexpectedly (not via LeaveSession).
    // Retries with capped backoff until it succeeds or LeaveSession cancels it -- no attempt
    // limit, since "Leave session" is always the user's escape hatch.
    private void BeginReconnect()
    {
        if (SessionCode == null || RelayUrl == null || reconnecting) return;
        DiagnosticLog.Info($"[Multiplayer] Connection to {RelayUrl} lost -- beginning reconnect loop for session {SessionCode}.");
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
            DiagnosticLog.Info($"[Multiplayer] Reconnect attempt {ReconnectAttempt + 1} in {delay.TotalSeconds}s.");
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            var client = new RelayClient();
            WireRelay(client);
            // Captured directly, not via OnDisconnectedOffThread -- that handler ignores this
            // client until it's actually installed as `relay` (see its own ReferenceEquals
            // guard), so it's the wrong place to learn WHY this particular attempt failed.
            Exception? failure = null;
            client.Disconnected += e => failure = e;
            await client.ConnectAsync(relayUrl, sessionCode, relayAccessToken).ConfigureAwait(false);
            var connected = client.IsConnected;
            DiagnosticLog.Info($"[Multiplayer] Reconnect attempt {ReconnectAttempt + 1}: {(connected ? "succeeded" : "failed")}.");

            if (!connected && failure is RelaySessionRejectedException rejected)
            {
                // The relay is reachable and has explicitly said this session doesn't exist --
                // most likely it restarted and forgot every room. Retrying the same code can
                // never succeed, so stop looping (this used to retry every 15s forever with
                // nothing to show for it -- see AnoMech-DamageDebug transcripts) and tell the
                // user plainly instead of leaving them staring at "Reconnect attempt N" forever.
                client.Dispose();
                DiagnosticLog.Warn($"[Multiplayer] Giving up on session {sessionCode} -- relay says: {rejected.Message}.");
                var wasHost = IsHost;
                _ = Plugin.Framework.Run(() =>
                {
                    LeaveSessionInternal(notifyOthers: false);
                    SessionEndReason = wasHost
                        ? $"The relay lost this session ({rejected.Message}) -- start a new one."
                        : $"The relay lost this session ({rejected.Message}) -- ask the host to start a new one.";
                    LobbyChanged?.Invoke();
                });
                return;
            }

            _ = Plugin.Framework.Run(() => FinishReconnectAttempt(client, connected, token));
            if (connected) return;
            ReconnectAttempt++;
        }
    }

    // Runs on the framework thread. `connected` was already read synchronously, so only
    // installing the result into game-visible state needs marshalling.
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
        disconnectedSinceMs = null;
        DiagnosticLog.Info($"[Multiplayer] Reconnected to session {SessionCode}.");
        lastHostMessageMs = Environment.TickCount64;
        ConnectionError = null;
        // Re-registers with the host (refreshes Names, triggers BroadcastLobbyState); the
        // existing LobbyStateMessage handler resumes a running scenario like a late join.
        if (!IsHost) _ = relay.SendAsync(new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum));
        LobbyChanged?.Invoke();
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

    // Peer-only: routes a Reset through the host (ResetRequestMessage) instead of resetting
    // only the requester's local view. The host's own Reset needs no equivalent.
    public void RequestReset()
    {
        if (IsHost || relay is not { IsConnected: true }) return;
        _ = relay.SendAsync(new ResetRequestMessage(MyPeerId));
    }

    // Peer-only: routes a Leave through the host (LeaveRequestMessage) so the whole group
    // ends the run together, rather than unloading this peer's zone while the host keeps
    // simulating for a puppet nobody has loaded. Distinct from LeaveSession, which
    // disconnects just the clicker.
    public void RequestLeaveInstance()
    {
        if (IsHost || relay is not { IsConnected: true }) return;
        _ = relay.SendAsync(new LeaveRequestMessage(MyPeerId));
    }

    private void ApplyClaim(Guid peerId, PartyRole role)
    {
        if (Session.ClaimedBy.TryGetValue(role, out var holder) && holder != peerId)
        {
            DiagnosticLog.Info($"[Multiplayer] Rejected role claim: {Session.NameOf(peerId)} wanted {role}, already held by {Session.NameOf(holder)}.");
            return;
        }
        // A build mismatch means differing scenario/protocol logic -- reject silently
        // (not a kick) so it self-resolves the moment the peer updates, no reconnect needed.
        if (IsVersionMismatched(peerId))
        {
            DiagnosticLog.Warn($"[Multiplayer] Rejected role claim from {Session.NameOf(peerId)} -- plugin build mismatch.");
            return;
        }
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.ClaimedBy[role] = peerId;
        DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} claimed {role}.");
        BroadcastLobbyState();
    }

    // "unknown" checksum (local checksumming failed) never counts as a mismatch either way --
    // fail open rather than block a session over a checksum that couldn't be computed.
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
        DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} released their role.");
        BroadcastLobbyState();
    }

    // Host-only: a non-host peer left. Unlike ApplyRelease this drops them from the roster
    // entirely (Names/Builds too), plus all host-only liveness/start-check bookkeeping.
    private void RemovePeer(Guid peerId)
    {
        var who = Session.NameOf(peerId);
        DiagnosticLog.Info($"[Multiplayer] Removing {who} ({peerId}) from the session (running={running}).");
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
        {
            Session.ClaimedBy.Remove(r);
            // A departed peer sends no more reports -- clear their banked shield so it
            // doesn't linger onto whoever claims this role next.
            TankShieldTracker.SetFromPeerReport(r, 0f);
        }
        Session.Names.Remove(peerId);
        Session.Builds.Remove(peerId);
        peerLastSeenMs.Remove(peerId);
        peerLatencyMs.Remove(peerId);
        peerStatuses.Remove(peerId);
        peerMitigationStatusIds.Remove(peerId);
        warnedStalePeers.Remove(peerId);
        startCheckFailures.Remove(peerId);
        if (pendingStartResponses?.Remove(peerId) == true && pendingStartResponses.Count == 0)
            FinishStartCheck();
        // Mid-fight, a missing party member usually dooms the mechanic -- end the run for
        // the rest rather than fight on short. Tick()'s host branch does the actual
        // broadcast once it sees ActiveScenario == null.
        if (running && Plugin.GameInstance.World.Map.IsInInstance)
        {
            DiagnosticLog.Info($"[Multiplayer] Ending the run because {who} left mid-fight.");
            Plugin.GameInstance.Leave();
        }
        BroadcastLobbyState();
    }

    private void BroadcastLobbyState()
    {
        LobbyChanged?.Invoke();
        _ = relay?.SendAsync(Session.ToMessage());
    }

    // ---- Starting the scenario ---------------------------------------------

    // Same preconditions RunScenarioInternal enforces, checked client-side up front so a
    // failure produces an immediate message instead of a silent no-op. Null when ready.
    // Also gates a claimed tank role on actually being on a tank job -- job-aware bot
    // mitigation picks ability ids off whoever's in the seat. Pre-start only; a mid-run job
    // swap isn't caught here (see JobForRole's Paladin fallback for that case).
    private string? CheckOwnStartReadiness()
    {
        if (!ZoneSession.IsInInn()) return "not in an inn";
        if (ZoneSession.IsPlayerBusy()) return "busy";
        if (MyClaimedRole is { } role && role.IsTank())
        {
            var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
            if (!PartyPresets.SkipRoleForJob(jobId).IsTank())
                return "queued as a tank role but not on a tank job";
        }
        return null;
    }

    // Host only. myRole must already be claimed -- there is no spectator mode, the engine
    // always seats this client's real character into a party slot.
    //
    // Doesn't start immediately: broadcasts StartCheckMessage and waits for every claimed
    // peer to confirm readiness (see FinishStartCheck), so a peer who isn't in an inn fails
    // loudly here instead of silently seconds later.
    public void StartScenario()
    {
        if (!IsHost || relay == null) return;
        if (!IsConnected)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: not connected to the relay.");
            return;
        }
        if (MyClaimedRole == null)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: host has not claimed a role.");
            return;
        }
        // Same scenario/strat/waymark a solo Start would use, so every peer's
        // OnStartReceived resolves the identical selection via LobbyStateMessage.
        if (Plugin.MainWindow.SelectedScenario is not { } selectedScenario
            || !selectedScenario.SupportsMultiplayer)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no multiplayer-supported scenario is selected in the main window.");
            return;
        }
        // A grouped scenario can leave SelectedStrat at -1 when the region has no strats;
        // broadcasting that would crash a debug-bot peer indexing AiStrats[SelectedAi].
        if (!Plugin.MainWindow.HasStartableStrat())
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no strat available for the selected scenario/region.");
            return;
        }
        var scenarioIndex = Plugin.GameInstance.Scenarios.ToList().IndexOf(selectedScenario);
        Session.ScenarioIndex = scenarioIndex;
        Session.SelectedAi = Plugin.MainWindow.SelectedStrat;
        Session.SelectedWaymark = Plugin.MainWindow.SelectedWaymark;
        // Belt-and-suspenders on top of ApplyClaim's own rejection (closes a race window).
        if (Session.ClaimedBy.Values.Any(IsVersionMismatched))
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: one or more claimed players are on a different plugin build.");
            return;
        }
        if (IsStartCheckPending) return; // already mid-check from a previous click

        if (CheckOwnStartReadiness() is { } ownReason)
        {
            StartCheckFailureReason = $"You cannot start: {ownReason}.";
            DiagnosticLog.Info($"[Multiplayer] Cannot start: {ownReason}.");
            LobbyChanged?.Invoke();
            return;
        }

        StartCheckFailureReason = null;
        startCheckFailures.Clear();
        startCheckTimer = 0f;
        pendingStartResponses = Session.ClaimedBy.Values.Where(id => id != MyPeerId).ToHashSet();
        // Don't wait out the full timeout for someone already known gone.
        foreach (var peerId in pendingStartResponses.ToList())
        {
            if (!IsPeerStale(peerId)) continue;
            startCheckFailures[peerId] = "disconnected";
            pendingStartResponses.Remove(peerId);
        }
        DiagnosticLog.Info($"[Multiplayer] Start requested -- waiting on readiness from: {string.Join(", ", pendingStartResponses.Select(Session.NameOf))}.");
        _ = relay.SendAsync(new StartCheckMessage());
        LobbyChanged?.Invoke();

        if (pendingStartResponses.Count == 0) FinishStartCheck();
    }

    // Host only: called once every claimed peer has answered (or Tick()'s timeout gave up).
    private void FinishStartCheck()
    {
        pendingStartResponses = null;
        if (startCheckFailures.Count > 0)
        {
            var summary = string.Join(", ", startCheckFailures.Select(kv => $"{Session.NameOf(kv.Key)} ({kv.Value})"));
            StartCheckFailureReason = $"{startCheckFailures.Count} player(s) cannot start: {summary}.";
            DiagnosticLog.Info($"[Multiplayer] Start check failed: {StartCheckFailureReason}");
            LobbyChanged?.Invoke();
            return;
        }
        DiagnosticLog.Info("[Multiplayer] Start check passed -- starting the scenario.");
        ActuallyStartScenario();
    }

    private void ActuallyStartScenario()
    {
        if (MyClaimedRole is not { } myRole) return; // re-checked defensively; shouldn't change mid-check

        var scenario = Plugin.GameInstance.Scenarios[Session.ScenarioIndex];
        var networkRoles = Session.ClaimedBy.Where(kv => kv.Value != MyPeerId).Select(kv => kv.Key).ToHashSet();
        DiagnosticLog.Info($"[Multiplayer] Host starting '{scenario.Name}' as {myRole}. Network roles: {string.Join(", ", networkRoles.Select(r => $"{r}={Session.NameOf(Session.ClaimedBy[r])}"))}.");

        Session.Started = true;
        _ = relay!.SendAsync(Session.ToMessage());
        _ = relay.SendAsync(new StartMessage());

        hostEnemyNetIds.Clear();
        hostEnemyLastLoggedModelState.Clear();
        hostEnemyLastLoggedStatuses.Clear();
        hostEnemyLastLoggedAnimationTimeline.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        nextEnemyNetId = 0;
        nextTetherNetId = 0;
        nextEventObjectNetId = 0;
        warnedStalePeers.Clear();
        aiReplayStateSent = false;
        pendingEndResendReturnedToInn = null;
        hostScenarioStarted = false;
        var nowMs = Environment.TickCount64;
        foreach (var peerId in Session.ClaimedBy.Values)
            if (peerId != MyPeerId)
                peerLastSeenMs[peerId] = nowMs;
        Plugin.GameInstance.PartyMemberKilled += OnPartyMemberKilledHost;
        Plugin.GameInstance.RunScenarioAsHost(scenario, myRole, Session.SelectedAi, Session.SelectedWaymark, networkRoles);
        // RunScenarioAsHost's scenario.Run already scheduled the chosen Ai's full
        // choreography against every role including the host's own; this flag is what stops
        // PlayerMovement.MoveTo from no-op'ing those calls for the host's own character.
        if (debugBotControlled)
        {
            DiagnosticLog.Info("[Multiplayer] Host: debug-bot mode active for own character this run.");
            DebugBotControl.Enabled = true;
        }
        running = true;
        LobbyChanged?.Invoke();
    }

    private void OnStartReceived()
    {
        // Idempotent: a fresh start delivers both LobbyStateMessage(Started=true) and
        // StartMessage in quick succession, and the LobbyStateMessage handler also calls
        // this directly for a late join/reconnect.
        if (IsHost) return;
        if (running)
        {
            DiagnosticLog.Debug("[Multiplayer] OnStartReceived: already running -- ignoring (idempotency guard).");
            return;
        }
        if (MyClaimedRole is not { } myRole)
        {
            DiagnosticLog.Warn("[Multiplayer] Host started the scenario, but I never claimed a role -- ignoring.");
            return;
        }

        var scenario = Plugin.GameInstance.Scenarios[Session.ScenarioIndex];
        var networkRoles = Enum.GetValues<PartyRole>().Where(r => r != myRole).ToHashSet();
        DiagnosticLog.Info($"[Multiplayer] Peer entering '{scenario.Name}' as {myRole}.");

        peerEnemies.Clear();
        peerEnemyModelState.Clear();
        peerEnemyLastLoggedStatuses.Clear();
        peerEnemyAnimationTimeline.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerEnemyLastCastSeq.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerEnteredInstance = false;
        StopDebugBotReplay();
        Plugin.GameInstance.RunScenarioAsPeer(scenario, myRole, Session.SelectedWaymark, networkRoles);
        running = true;
    }

    // Describes who/what occupies a role for logging purposes -- the local real player (with
    // their real job), a network puppet (a peer, with the job they connected as), or a bot.
    private string DescribeRoleOwner(PartyRole role, SimCharacter? member)
    {
        if (member == null) return "empty";
        if (member is SimNetworkPuppet puppet) return $"{puppet.DisplayName}, job {puppet.ClassJob}";
        if (ReferenceEquals(member, Plugin.GameInstance.World.Party.Player))
            return $"{DisplayName} (me), job {Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId.ToString() ?? "?"}";
        return "bot";
    }

    // Shared by all four status-broadcast paths -- logs one line per status gained/lost/
    // restacked instead of one "set changed" summary. `lastSeen` is mutated in place.
    private static void LogStatusChanges(string who, IReadOnlyList<(ushort StatusId, ushort Stacks, float RemainingTime)> current, Dictionary<ushort, ushort> lastSeen)
    {
        var currentIds = new HashSet<ushort>();
        foreach (var (id, stacks, remaining) in current)
        {
            currentIds.Add(id);
            if (!lastSeen.TryGetValue(id, out var lastStacks))
                DiagnosticLog.Info($"[Multiplayer] {who}: status {id} gained (stacks={stacks}, duration={remaining:F1}).");
            else if (lastStacks != stacks)
                DiagnosticLog.Info($"[Multiplayer] {who}: status {id} stacks {lastStacks}->{stacks}.");
            lastSeen[id] = stacks;
        }
        foreach (var id in lastSeen.Keys.Where(id => !currentIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] {who}: status {id} lost.");
            lastSeen.Remove(id);
        }
    }

}
