using System;
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

// Owns the multiplayer session lifecycle and the host<->peer replication loop.
// One host runs the real UMAD P3 Black Hole simulation unmodified (RNG, AI,
// DamageSolver, the whole engine exactly as solo play uses it) with joined
// peers' claimed roles spawned as SimNetworkPuppet instead of AI bots
// (PartyCreator). Peers run zero scenario logic themselves -- they load the
// same cosmetic zone/party, then just apply whatever the host broadcasts:
//   - WorldSnapshot (every host Tick, so tied to the host's own FPS): enemy/
//     tether/role poses and casts, replayed
//     through the same public SimWorld/SimEnemy APIs scenarios use, so a
//     peer's local doppels get the real cast-bar/omen/tether VFX pipeline
//     rather than a hand-rolled visual.
//   - RoleKilled: routed through the same Game.Kill every death already
//     funnels through, targeting whatever locally occupies that role (the
//     peer's own real SimPlayer, or that role's local puppet).
// Peers report their own real position back every frame (SelfPose) so the
// host's puppet for that peer stays where DamageSolver's spatial queries
// expect it.
//
// All engine calls in this class assume the framework thread (Game.Tick,
// World.SpawnEnemy, SetPosition, etc. are not thread-safe) -- Tick() runs
// there because Plugin drives it from OnFrameworkUpdate, and every handler
// reached from RelayClient.MessageReceived (a background receive thread) is
// marshalled onto it via Plugin.Framework.Run before touching any game state.
public sealed class MultiplayerManager : IDisposable
{
    // No throttle here (was a fixed interval, 12Hz then 24Hz): Tick only runs once
    // per Framework update in the first place, so any interval smaller than the
    // actual frame time (typically ~4-17ms depending on the host's FPS) can never
    // fire more than once per frame anyway -- there's no fresher data to send in
    // between. A timer value only matters once it's *larger* than a frame, at
    // which point it's a deliberate throttle, not a floor. For a tank reading
    // boss facing/position in real time, every millisecond of artificial delay
    // stacks on top of whatever the relay's real transit time already costs, so
    // just broadcast every Tick and let the host's own frame rate be the ceiling.
    // Tradeoff is outbound snapshot bandwidth/CPU scaling with the host's FPS
    // instead of being capped -- fine for a handful of peers.
    // No throttle here either (was 1/15, 15Hz) -- same reasoning as the enemy
    // snapshot rate above: a peer's own Tick only runs once per their own frame,
    // so this can't fire more than once per frame regardless of the timer value,
    // and every millisecond of added staleness here is the host's belief about
    // where a real player currently is, which feeds directly into host-side
    // mechanic checks (e.g. UMAD P3's DamageDown-for-standing-on-a-black-hole
    // check) as well as what other peers see. SelfPoseMessage is a single small
    // message (one GUID + 4 floats) rather than a full WorldSnapshotMessage, so
    // the absolute bandwidth cost per peer is much smaller than the enemy-side
    // change -- it just scales with the number of connected peers instead of
    // being capped at a fixed rate.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    // Mirrors UmadP5ExaflaresScenario.FrameGapCapSeconds -- a peer's P5 debug-bot
    // replay ticks debugShadowStateP5.Timeline off this Tick's own deltaSeconds
    // (see Tick()'s peer branch), which has no equivalent hitch guard of its own;
    // without this, a pause/alt-tab/loading-stall frame would fire every event
    // still queued on that timeline at once instead of skipping the frame like
    // the real scenario's wall-clock Stopwatch does.
    private const float P5ReplayFrameGapCapSeconds = 0.25f;

    // Scenarios a multiplayer session can host/join/start -- gates MainWindow's
    // "Multiplayer..." button and StartScenario's own validation. Core
    // replication (enemies/tethers/roles/statuses/ModelState) works for any
    // IScenario automatically; this list exists only because debug-bot AI
    // replay (TrySendAiReplayState/TryStartDebugBotReplay) needs a hand-written
    // per-scenario wire message and shadow-state factory, so a scenario has to
    // be deliberately added here once that's been done for it.
    public static readonly Type[] SupportedScenarios =
    [
        typeof(UmadP2ForsakenScenario),
        typeof(UmadP3BlackHoleScenario),
        typeof(UmadP4KefkaSaysScenario),
        typeof(UmadP5ExaflaresScenario),
    ];

    private RelayClient? relay;
    private bool running;

    private readonly Dictionary<SimEnemy, int> hostEnemyNetIds = new();
    private int nextEnemyNetId;
    private readonly Dictionary<SimTether, int> hostTetherNetIds = new();
    private int nextTetherNetId;
    // Host-only: last ModelState we logged per enemy, purely so
    // SampleAndBroadcastSnapshot can log the edge (a mid-fight SetModelState
    // call, e.g. Kefka's grow transformation) instead of every single sample
    // -- ModelState itself is still sent in full every snapshot regardless.
    private readonly Dictionary<SimEnemy, byte> hostEnemyLastLoggedModelState = new();
    // Host-only: same edge-triggered-logging-only purpose as above, for
    // AddStatus/RemoveStatus calls (e.g. UMAD P3's stack-based "Max" grow
    // status) -- keyed on a flattened "id:stacks,id:stacks" string since
    // ActiveStatusSnapshot's list identity changes every call regardless of
    // content.
    private readonly Dictionary<SimEnemy, string> hostEnemyLastLoggedStatuses = new();
    // Host-only: same edge-triggered-logging-only purpose, for
    // PlayAnimationTimeline/AttachLockonVfx calls. Absent from the dictionary
    // == "never logged yet" (distinct from "logged 0"), since 0 is a
    // meaningful default-y value for both fields.
    private readonly Dictionary<SimEnemy, ushort> hostEnemyLastLoggedAnimationTimeline = new();
    private readonly Dictionary<SimEnemy, uint> hostEnemyLastLoggedLockonVfx = new();
    // Host-only: same edge-triggered-logging-only purpose, for party-role
    // AddStatus/RemoveStatus and AttachLockonVfx calls -- keyed by PartyRole
    // (fixed 8-entry set) rather than a Dictionary<SimCharacter,...>, since
    // whichever SimCharacter occupies a role changes across a claim/release
    // but the role itself doesn't.
    private readonly Dictionary<PartyRole, string> hostRoleLastLoggedStatuses = new();
    private readonly Dictionary<PartyRole, uint> hostRoleLastLoggedLockonVfx = new();

    private readonly Dictionary<SimEventObject, int> hostEventObjectNetIds = new();
    private int nextEventObjectNetId;

    private readonly Dictionary<int, SimEnemy> peerEnemies = new();
    private readonly Dictionary<int, SimTether> peerTethers = new();
    private readonly Dictionary<int, SimEventObject> peerEventObjects = new();
    // Peer-only: last ModelState actually applied per NetId, so SetModelState
    // is only re-issued on a genuine change -- its native rebuild briefly
    // disables/re-enables drawing (see SimEnemy's EnemyListMode doc), so
    // calling it every snapshot even when unchanged would flicker.
    private readonly Dictionary<int, byte> peerEnemyModelState = new();
    // Peer-only: last status set logged per NetId, edge-triggered like
    // peerEnemyModelState above -- statuses are still reconciled against the
    // broadcast every snapshot regardless, this only gates the log line.
    private readonly Dictionary<int, string> peerEnemyLastLoggedStatuses = new();
    // Peer-only: last-applied AnimationTimelineId/LastLockonVfxId per enemy
    // NetId, edge-triggered like peerEnemyModelState -- re-issuing
    // PlayAnimationTimeline/AttachLockonVfx every snapshot even when
    // unchanged would restart the same animation/VFX on a loop.
    private readonly Dictionary<int, ushort> peerEnemyAnimationTimeline = new();
    private readonly Dictionary<int, uint> peerEnemyLastLockonVfx = new();
    // Peer-only: last-seen LastInstantCastSeq per enemy NetId, edge-triggered the
    // same way -- see EnemyState.LastInstantCastSeq's doc comment for why instant
    // casts need this separate counter instead of the IsCasting rising edge.
    private readonly Dictionary<int, int> peerEnemyLastInstantCastSeq = new();
    // Peer-only: last-applied CurrentState per event-object NetId, edge-triggered
    // the same way -- SetState is a plain field write with no native rebuild to
    // worry about, but re-issuing it unconditionally every snapshot is still
    // pointless churn once it's already correct.
    private readonly Dictionary<int, ushort> peerEventObjectState = new();
    // Peer-only role equivalents of the *Statuses/LastLockonVfx pairs above --
    // keyed by PartyRole like their host-side counterparts. Reconciled/applied
    // for every role INCLUDING the peer's own claimed one: unlike position
    // (self-authoritative -- see OnWorldSnapshotReceived), a peer never runs
    // any scenario logic themselves, so nothing else would ever call
    // AddStatus/AttachLockonVfx against their own real character.
    private readonly Dictionary<PartyRole, string> peerRoleLastLoggedStatuses = new();
    private readonly Dictionary<PartyRole, uint> peerRoleLastLockonVfx = new();
    // Peer-only: statusIds WE ourselves applied to a role via reconciliation
    // below, as opposed to a status the local game client manages entirely on
    // its own -- e.g. LocalPlayerInputHooks applies the real Sprint buff
    // directly to Plugin.GameInstance.Player on a keypress, which is the exact
    // same object as world.Party.Get(role) for the peer's own claimed role
    // (SimParty.Player => Get(PlayerRole)). Unlike enemies (nothing local ever
    // touches an enemy's statuses independent of the host's broadcast), a
    // peer's own real character can have statuses applied by something other
    // than this reconciliation, so removal here must only ever undo what THIS
    // code added -- diffing raw ActiveStatusSnapshot against the broadcast
    // (like the enemy path does) would strip Sprint the instant it's pressed.
    private readonly Dictionary<PartyRole, HashSet<ushort>> peerRoleReconciledStatusIds = new();
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

    // ---- Pre-Start readiness check -----------------------------------------
    // Host-only: mid-flight state for a StartScenario call while it's waiting
    // on every claimed peer's StartCheckResponseMessage -- see StartScenario,
    // FinishStartCheck, and the timeout handling in Tick().
    private const float StartCheckTimeoutSeconds = 5f;
    private HashSet<Guid>? pendingStartResponses;
    private readonly Dictionary<Guid, string> startCheckFailures = new();
    private float startCheckTimer;
    public bool IsStartCheckPending => pendingStartResponses != null;
    public string? StartCheckFailureReason { get; private set; }

    // ---- Debug: bot-controlled host or peer ---------------------------------
    // Testing aid: whoever's using it (host or peer) can have their own
    // claimed role driven locally by the exact same AiManager/scenario-Ai
    // choreography a bot in that role would produce, instead of a real
    // person, so one developer can fill a multi-person session without
    // needing real people in every slot. For the host this is direct: their
    // own scenario.Run already scheduled that choreography against the real
    // (live) state for every role -- see ActuallyStartScenario -- flipping
    // DebugBotControl.Enabled just lets PlayerMovement.MoveTo stop no-op'ing
    // for it. For a peer it's reconstructed from a broadcast AiReplayStateMessage
    // (see TrySendAiReplayState/TryStartDebugBotReplay), since a peer never
    // runs scenario.Run at all. Sticky across multiple Start/Reset cycles in
    // the same session (only cleared on LeaveSession) so a tester doesn't have
    // to re-toggle it before every run; gated to lobby-only via
    // SetDebugBotControlled since the choreography timeline only makes sense
    // replayed from a fresh Start.
    private bool debugBotControlled;
    public bool DebugBotControlled => debugBotControlled;

    // Host-only: whether AiReplayStateMessage has already gone out for the
    // current run -- edge-triggered against UmadP3BlackHoleScenario.LastState,
    // which (like ActiveScenario) isn't guaranteed populated on the very first
    // Tick() after StartScenario, since RunScenarioAsHost's real work is
    // deferred via Plugin.Framework.Run.
    private bool aiReplayStateSent;

    // Host-only: same deferred-Framework.Run race as peerEnteredInstance below,
    // but on the host's own "has my run actually started" check -- running is
    // set true synchronously in ActuallyStartScenario, but RunScenarioAsHost's
    // real work (setting Game.ActiveScenario) runs later on a Framework.Run
    // callback. Without this, a Tick() landing in that window sees
    // ActiveScenario still null, wrongly concludes the just-started run
    // already ended, and broadcasts a bogus EndMessage/LobbyState(Started=
    // false) moments after the real Start -- which a peer can race into a
    // stray second RunScenarioAsPeer call (see OnStartReceived).
    private bool hostScenarioStarted;

    // Peer-only: the host's broadcast AI-replay values, buffered until our own
    // zone/party is actually ready (peerEnteredInstance) -- arrival order
    // between the two isn't guaranteed, so both TryStartDebugBotReplay call
    // sites (Dispatch and Tick) funnel through the same check. debugShadowState
    // is kept around after replay starts so OnWorldSnapshotReceived can keep
    // resolving ScenarioObjects.Chaos/Exdeath from newly-seen enemies if they
    // weren't replicated yet the moment replay began.
    private AiReplayStateMessage? pendingAiReplayState;
    private UmadP3BlackHoleState? debugShadowState;
    // Per-scenario siblings of the two fields above (see TryStartDebugBotReplay) --
    // only one is ever non-null in a given run, matching whichever scenario is
    // actually active; kept separate rather than a shared base type since each
    // scenario's shadow-state shape/fields differ (same reasoning as
    // MultiplayerManager.SupportedScenarios' comment on why this isn't a
    // generic/opaque-payload abstraction).
    private P2AiReplayStateMessage? pendingP2AiReplayState;
    private UmadP2ForsakenState? debugShadowStateP2;
    private P4AiReplayStateMessage? pendingP4AiReplayState;
    private UmadP4KefkaSaysState? debugShadowStateP4;
    private P5AiReplayStateMessage? pendingP5AiReplayState;
    private UmadP5ExaflaresState? debugShadowStateP5;
    private bool debugBotReplayStarted;

    // Lobby-only: refuses to change anything while running, so the toggle
    // can't flip mid-fight -- flipping it after the choreography's already
    // been scheduled (against the host's own live state, or a peer's replayed
    // shadow state) would just silently do nothing useful anyway, so refusing
    // outright is more honest than a no-op success.
    public bool SetDebugBotControlled(bool value)
    {
        if (running) return false;
        debugBotControlled = value;
        return true;
    }

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
    // Host-only: when the host's OWN connection to the relay went down (wall
    // clock), null while connected. Symmetric to how a peer judges the host
    // dead via IsHostStale/SecondsSinceHostMessage -- without this the host
    // just keeps retrying to reconnect forever (BeginReconnect has no attempt
    // limit by design) while the fight keeps running locally, oblivious that
    // every peer has independently already given up and left via their own
    // IsHostStale timeout. Set in OnDisconnectedOffThread, cleared on a
    // successful FinishReconnectAttempt; read by Tick()'s host-stale check.
    private long? disconnectedSinceMs;
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

        DiagnosticLog.Info($"[Multiplayer] Hosting session {SessionCode} at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
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

        DiagnosticLog.Info($"[Multiplayer] Joining session {SessionCode} at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
        relay = new RelayClient();
        WireRelay(relay);
        _ = ConnectAndHelloAsync(relayUrl, SessionCode);
    }

    private async Task ConnectAndHelloAsync(string relayUrl, string code)
    {
        await relay!.ConnectAsync(relayUrl, code);
        DiagnosticLog.Info($"[Multiplayer] Connected to relay, socket ready -- sending Hello.");
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
        if (SessionCode != null)
            DiagnosticLog.Info($"[Multiplayer] Leaving session {SessionCode} (was {(IsHost ? "host" : "peer")}, notifyOthers={notifyOthers}).");
        reconnectCts?.Cancel();
        reconnectCts?.Dispose();
        reconnectCts = null;
        reconnecting = false;
        ReconnectAttempt = 0;
        disconnectedSinceMs = null;
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
        hostEnemyLastLoggedModelState.Clear();
        hostEnemyLastLoggedStatuses.Clear();
        hostEnemyLastLoggedAnimationTimeline.Clear();
        hostEnemyLastLoggedLockonVfx.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostRoleLastLoggedLockonVfx.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        peerEnemies.Clear();
        peerEnemyModelState.Clear();
        peerEnemyLastLoggedStatuses.Clear();
        peerEnemyAnimationTimeline.Clear();
        peerEnemyLastLockonVfx.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleLastLockonVfx.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerLastSeenMs.Clear();
        peerLatencyMs.Clear();
        peerStatuses.Clear();
        warnedStalePeers.Clear();
        pingTimer = 0f;
        pendingStartResponses = null;
        startCheckFailures.Clear();
        StartCheckFailureReason = null;
        startCheckTimer = 0f;
        debugBotControlled = false;
        aiReplayStateSent = false;
        StopDebugBotReplay();
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
            await client.ConnectAsync(relayUrl, sessionCode).ConfigureAwait(false);
            var connected = client.IsConnected;
            DiagnosticLog.Info($"[Multiplayer] Reconnect attempt {ReconnectAttempt + 1}: {(connected ? "succeeded" : "failed")}.");

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
        disconnectedSinceMs = null;
        DiagnosticLog.Info($"[Multiplayer] Reconnected to session {SessionCode}.");
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

    // Peer-only: MainWindow's plain Leave button routes here instead of
    // calling Game.Leave() directly while connected as a peer -- calling it
    // locally would unload the peer's own zone without telling the host,
    // leaving the host still simulating/broadcasting to a puppet-driven world
    // that peer no longer has loaded. Routing through the host instead ends
    // the run for the whole group (see LeaveRequestMessage) exactly like
    // RequestReset does, sending everyone -- including this peer, once the
    // resulting EndMessage broadcast reaches them -- back to the inn, with
    // the session itself untouched (distinct from LeaveSession, which
    // disconnects the clicker entirely). The host's own Leave click needs no
    // equivalent: it's already authoritative and already propagates via the
    // existing Tick()/EndMessage path.
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
        // A build mismatch means differing scenario/protocol logic between host
        // and this peer -- letting them into a slot is exactly how the desyncs
        // this project has repeatedly chased happen. Silently rejecting (rather
        // than kicking) is deliberate: it's recoverable the moment they update,
        // no reconnect needed, and IsVersionMismatched is also surfaced in the
        // UI so it isn't a mystery why nothing happened.
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
        DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} released their role.");
        BroadcastLobbyState();
    }

    // Host-only: a non-host peer left (see the SessionEndedMessage case in
    // Dispatch) -- unlike ApplyRelease this drops them from the roster
    // entirely (Names/Builds too, not just their role claim) since they're
    // gone, not just unclaimed, plus all the host-only liveness/start-check
    // bookkeeping keyed on their PeerId.
    private void RemovePeer(Guid peerId)
    {
        var who = Session.NameOf(peerId);
        DiagnosticLog.Info($"[Multiplayer] Removing {who} ({peerId}) from the session (running={running}).");
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.Names.Remove(peerId);
        Session.Builds.Remove(peerId);
        peerLastSeenMs.Remove(peerId);
        peerLatencyMs.Remove(peerId);
        peerStatuses.Remove(peerId);
        warnedStalePeers.Remove(peerId);
        startCheckFailures.Remove(peerId);
        if (pendingStartResponses?.Remove(peerId) == true && pendingStartResponses.Count == 0)
            FinishStartCheck();
        // Mid-fight, a party member vanishing usually dooms whatever mechanic
        // they were meant to help resolve, so end the run for whoever's left
        // rather than have them fight on short a player against a simulation
        // that doesn't account for it. Just Leave() -- Tick()'s host branch
        // picks up the resulting ActiveScenario == null next frame and does
        // the actual broadcast (EndMessage + LobbyState(Started=false)),
        // exactly like any other end-of-run, sending the rest of the group
        // back to the inn while leaving the session (this method, the roster,
        // the relay connection) untouched behind them. IsInInstance guard:
        // Leave() -> Unload() assumes a zone was actually entered.
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

    // Shared "can I actually enter the scenario right now" check -- the same
    // preconditions RunScenarioInternal enforces (ZoneSession.IsInInn /
    // IsPlayerBusy), checked client-side up front so a failure produces an
    // immediate, specific message instead of RunScenarioInternal silently
    // no-oping with nothing but a local log line nobody else ever sees.
    // Returns null when ready, or a short human-readable reason otherwise.
    private static string? CheckOwnStartReadiness()
    {
        if (!ZoneSession.IsInInn()) return "not in an inn";
        if (ZoneSession.IsPlayerBusy()) return "busy";
        return null;
    }

    // Host only. myRole must already be claimed (see MyClaimedRole) -- there is
    // no spectator mode: the engine always seats "this client's real character"
    // into a party slot (PartyCreator.Populate), so the host plays too.
    //
    // Doesn't start immediately: broadcasts StartCheckMessage first and waits
    // for every claimed peer to confirm readiness (see FinishStartCheck) --
    // without this, a peer who isn't in an inn would just silently fail to
    // enter the instance seconds later with no signal to the host at all.
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
        // Captures whatever's currently selected in the main window -- same
        // scenario/strat/waymark a solo Start would use (Plugin.MainWindow.
        // Selected*) -- so ActuallyStartScenario and, via LobbyStateMessage,
        // every peer's OnStartReceived resolve the identical scenario/strat
        // instead of each independently guessing.
        if (Plugin.MainWindow.SelectedScenario is not { } selectedScenario
            || !SupportedScenarios.Contains(selectedScenario.GetType()))
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no multiplayer-supported scenario is selected in the main window.");
            return;
        }
        // Reuses MainWindow's own solo-Start gate: a grouped scenario (e.g. P2
        // Forsaken's NA/EU strats) can leave SelectedStrat at -1 when the
        // selected region has no strats. Without this check that -1 would get
        // broadcast as SelectedAi and later crash a debug-bot peer indexing
        // AiStrats[SelectedAi] in TryStartDebugBotReplay.
        if (!Plugin.MainWindow.HasStartableStrat())
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no strat available for the selected scenario/region.");
            return;
        }
        var scenarioIndex = Plugin.GameInstance.Scenarios.ToList().IndexOf(selectedScenario);
        Session.ScenarioIndex = scenarioIndex;
        Session.SelectedAi = Plugin.MainWindow.SelectedStrat;
        Session.SelectedWaymark = Plugin.MainWindow.SelectedWaymark;
        // Belt-and-suspenders on top of ApplyClaim's own rejection: closes the
        // narrow window where a peer's ClaimRoleMessage could theoretically
        // race ahead of their Hello (see IsVersionMismatched).
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

    // Host only: called once every claimed peer has answered StartCheckMessage
    // (or the timeout in Tick() gave up on whoever didn't). Either proceeds
    // with the real start or reports who's blocking it.
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
        hostEnemyLastLoggedLockonVfx.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostRoleLastLoggedLockonVfx.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        nextEnemyNetId = 0;
        nextTetherNetId = 0;
        nextEventObjectNetId = 0;
        warnedStalePeers.Clear();
        aiReplayStateSent = false;
        hostScenarioStarted = false;
        var nowMs = Environment.TickCount64;
        foreach (var peerId in Session.ClaimedBy.Values)
            if (peerId != MyPeerId)
                peerLastSeenMs[peerId] = nowMs;
        Plugin.GameInstance.PartyMemberKilled += OnPartyMemberKilledHost;
        Plugin.GameInstance.RunScenarioAsHost(scenario, myRole, Session.SelectedAi, Session.SelectedWaymark, networkRoles);
        // Host debug-bot mode needs no reconstruction (unlike a peer's
        // TryStartDebugBotReplay): RunScenarioAsHost above already runs the
        // scenario's own scenario.Run, which -- given a real SelectedAi index --
        // has already scheduled that Ai's full choreography (AiManager.Move/
        // Intercept calls) against every role, including the host's own,
        // through the live world.Events/AiManager. Those calls already reach
        // the host's own SimPlayer today; PlayerMovement.MoveTo is what
        // silently no-ops them unless this flag is set. Timing-safe regardless
        // of RunScenarioAsHost's own Plugin.Framework.Run deferral: this only
        // needs to be true before the Ai's *scheduled* moves actually fire,
        // seconds from now at the earliest, not before this line returns.
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
        // Idempotent: a genuine fresh start delivers both a LobbyStateMessage
        // (Started=true) and a StartMessage in quick succession, and the
        // LobbyStateMessage handler below also calls this directly for a late
        // join/reconnect -- without this guard a normal start would trigger
        // RunScenarioAsPeer twice.
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
        peerEnemyLastLockonVfx.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleLastLockonVfx.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerEnteredInstance = false;
        StopDebugBotReplay();
        Plugin.GameInstance.RunScenarioAsPeer(scenario, myRole, Session.SelectedWaymark, networkRoles);
        running = true;
    }

    // ---- Per-frame tick (framework thread; see Plugin.OnFrameworkUpdate) ----

    // Host-only local bookkeeping for "my own run just ended," shared by the
    // normal in-Tick detection (ActiveScenario went null via Reset/Leave/a
    // natural finish, still connected -- broadcasts EndMessage right after
    // this) and Tick()'s own-disconnection-timeout branch above (can't
    // broadcast anything, every peer has already reached the same conclusion
    // independently). Does NOT touch the relay/session/roster -- only that
    // the host's own local run bookkeeping is consistent again.
    private void EndHostRunLocally()
    {
        running = false;
        // Mirrors StopDebugBotReplay's peer-side reset -- without this, a
        // debug-bot host would stay bot-driven (or, on a fresh non-multiplayer
        // solo run afterward, only by luck not still be) past the fight ending.
        DebugBotControl.Enabled = false;
        Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
        // Without this, Session.Started stays true forever once a run ends --
        // the Start button in MultiplayerWindow is gated on !Started, so the
        // lobby would be permanently stuck past its first run with no way to
        // retry short of leaving and re-hosting a brand new session/code.
        Session.Started = false;
    }

    public void Tick(float deltaSeconds)
    {
        // Checked before the IsConnected early-return below (this is exactly the
        // case where relay is down) -- see disconnectedSinceMs's own doc comment.
        // One-shot: nulled out immediately so this doesn't re-fire (and re-call
        // Leave()) every subsequent tick while still disconnected/reconnecting.
        if (IsHost && disconnectedSinceMs is { } since && running && Plugin.GameInstance.World.Map.IsInInstance
            && Environment.TickCount64 - since > PeerStaleTimeoutMs)
        {
            DiagnosticLog.Warn($"[Multiplayer] Disconnected from the relay for over {PeerStaleTimeoutMs / 1000}s while running -- leaving the zone myself (every peer has likely already given up waiting and left on their own).");
            Plugin.GameInstance.Leave();
            EndHostRunLocally();
            disconnectedSinceMs = null;
            LobbyChanged?.Invoke();
        }

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

            if (pendingStartResponses is { Count: > 0 } pending)
            {
                startCheckTimer += deltaSeconds;
                if (startCheckTimer >= StartCheckTimeoutSeconds)
                {
                    DiagnosticLog.Info($"[Multiplayer] StartCheck timed out waiting on: {string.Join(", ", pending.Select(Session.NameOf))}.");
                    foreach (var peerId in pending)
                        startCheckFailures[peerId] = "no response";
                    FinishStartCheck();
                }
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
            // roster or a stale zone forever. IsInInstance guard: Leave() ->
            // Unload() assumes a zone was actually entered (it restores the
            // real character to the position ZoneSession.Enter() saved) --
            // running can briefly be true before that deferred entry actually
            // completes, and calling it too early teleports the real character
            // to garbage coordinates instead.
            DiagnosticLog.Warn($"[Multiplayer] Lost contact with the host (no message in {SecondsSinceHostMessage:F1}s, threshold {PeerStaleTimeoutMs / 1000}s) -- leaving.");
            if (running && Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
            LeaveSession();
            SessionEndReason = "Lost contact with the host.";
            LobbyChanged?.Invoke();
            return;
        }

        if (!running) return;

        if (IsHost)
        {
            // Edge-triggered against ActiveScenario becoming non-null (see
            // hostScenarioStarted) before "it's null" is allowed to mean "the
            // run ended" -- RunScenarioAsHost's deferred completion hasn't
            // necessarily set it yet on the first Tick() after Start.
            if (Plugin.GameInstance.ActiveScenario != null)
            {
                hostScenarioStarted = true;
            }
            else if (!hostScenarioStarted)
            {
                return;
            }
            else
            {
                // Reset/Leave clears ActiveScenario -- stop broadcasting once the local
                // run has ended rather than spamming empty snapshots (or, worse, a
                // later unrelated solo run) to peers who are still connected.
                EndHostRunLocally();
                _ = relay?.SendAsync(Session.ToMessage());
                // Reset() leaves World.Map.IsInInstance true (deliberately stays
                // in-zone); only Leave()/a natural finish clears it. Read here,
                // now, before anything else can change it.
                var returnedToInn = !Plugin.GameInstance.World.Map.IsInInstance;
                DiagnosticLog.Info($"[Multiplayer] Run ended (ReturnedToInn={returnedToInn}) -- broadcasting EndMessage.");
                _ = relay?.SendAsync(new EndMessage(ReturnedToInn: returnedToInn));
                LobbyChanged?.Invoke();
                return;
            }
            if (!aiReplayStateSent) TrySendAiReplayState();
            SampleAndBroadcastSnapshot();
        }
        else
        {
            if (Plugin.GameInstance.World.Map.IsInInstance)
            {
                if (!peerEnteredInstance) DiagnosticLog.Info("[Multiplayer] Peer's deferred zone entry completed -- now sending SelfPose.");
                peerEnteredInstance = true;
                // Cheap and idempotent past the first successful call (guarded
                // internally on debugBotReplayStarted) -- simpler than a second
                // edge-trigger flag alongside peerEnteredInstance.
                TryStartDebugBotReplay();
                // P5 Exaflares only: UmadP5ExaflaresAi schedules its dodges onto
                // debugShadowStateP5.Timeline (a private EventScheduler), which the
                // real scenario normally drives every frame from its own Tick (see
                // UmadP5ExaflaresScenario.Tick) -- but a peer never runs
                // IScenario.Tick at all (Game.RunScenarioInternal never sets
                // ActiveScenario for isPeer:true), so nothing else would ever
                // advance it. Mirrors that method's two calls exactly, using the
                // same raw (EventTimeScale-independent) deltaSeconds this Tick
                // already receives from Plugin.OnFrameworkUpdate -- capped the
                // same way UmadP5ExaflaresScenario.Tick caps its own wall-clock
                // delta, so a hitch/alt-tab frame skips this tick instead of
                // dumping every still-queued event out at once.
                if (debugShadowStateP5 is { } p5Shadow && deltaSeconds > 0f && deltaSeconds <= P5ReplayFrameGapCapSeconds)
                {
                    p5Shadow.Timeline.Tick(deltaSeconds);
                    p5Shadow.SpreadTick?.Invoke(deltaSeconds);
                }
            }
            else if (peerEnteredInstance)
            {
                DiagnosticLog.Info("[Multiplayer] Peer's zone was unloaded out from under the run (IsInInstance went false) -- stopping locally.");
                running = false;
                StopDebugBotReplay();
                return;
            }
            else
            {
                // Zone load queued by RunScenarioAsPeer hasn't run yet -- wait
                // rather than tearing down a run that hasn't truly started.
                return;
            }
            SendSelfPose();
        }
    }

    // ---- Host: sampling the live simulation --------------------------------

    private void SampleAndBroadcastSnapshot()
    {
        var world = Plugin.GameInstance.World;

        var liveEnemies = world.Children.OfType<SimEnemy>().Where(e => e.IsActive).ToList();
        foreach (var stale in hostEnemyNetIds.Keys.Where(e => !liveEnemies.Contains(e)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: enemy NetId {hostEnemyNetIds[stale]} ({stale.BNpcBaseId}) no longer active -- dropping from broadcast.");
            hostEnemyNetIds.Remove(stale);
            hostEnemyLastLoggedModelState.Remove(stale);
            hostEnemyLastLoggedStatuses.Remove(stale);
            hostEnemyLastLoggedAnimationTimeline.Remove(stale);
            hostEnemyLastLoggedLockonVfx.Remove(stale);
        }

        var enemies = new List<EnemyState>(liveEnemies.Count);
        foreach (var enemy in liveEnemies)
        {
            if (!hostEnemyNetIds.TryGetValue(enemy, out var netId))
            {
                netId = nextEnemyNetId++;
                hostEnemyNetIds[enemy] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new enemy NetId {netId} -- BNpcBase {enemy.BNpcBaseId}, pos {enemy.Position}, visible {enemy.Visible}.");
            }
            var cfg = enemy.SpawnConfig;
            var modelState = enemy.ModelState;
            if (!hostEnemyLastLoggedModelState.TryGetValue(enemy, out var lastLogged) || lastLogged != modelState)
            {
                hostEnemyLastLoggedModelState[enemy] = modelState;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) ModelState -> 0x{modelState:X2}.");
            }
            var statusSnapshot = enemy.ActiveStatusSnapshot;
            var statusKey = string.Join(",", statusSnapshot.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!hostEnemyLastLoggedStatuses.TryGetValue(enemy, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                hostEnemyLastLoggedStatuses[enemy] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) statuses -> [{statusKey}].");
            }
            if (enemy.AnimationTimelineId is { } timelineId
                && (!hostEnemyLastLoggedAnimationTimeline.TryGetValue(enemy, out var lastTimeline) || lastTimeline != timelineId))
            {
                hostEnemyLastLoggedAnimationTimeline[enemy] = timelineId;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) AnimationTimelineId -> 0x{timelineId:X4}.");
            }
            if (enemy.LastLockonVfxId is { } lockonId
                && (!hostEnemyLastLoggedLockonVfx.TryGetValue(enemy, out var lastLockon) || lastLockon != lockonId))
            {
                hostEnemyLastLoggedLockonVfx[enemy] = lockonId;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) LastLockonVfxId -> {lockonId}.");
            }
            var (castTargetEnemyNetId, castTargetRole) = ResolveTargetId(world, enemy.CastTargetId);
            var (instantTargetEnemyNetId, instantTargetRole) = ResolveTargetId(world, enemy.LastInstantCastTargetId);
            enemies.Add(new EnemyState(
                netId, enemy.BNpcBaseId, cfg.NameId, cfg.Level, cfg.Targetable, enemy.EnemyListMode,
                cfg.ModelCharaId, cfg.Scale, cfg.HitboxRadius, cfg.InitialModeAttributeFlags, enemy.Visible, modelState,
                statusSnapshot.Select(s => new EnemyStatusState(s.StatusId, s.Stacks)).ToList(),
                enemy.AnimationTimelineId, enemy.LastLockonVfxId,
                enemy.Position.X, enemy.Position.Y, enemy.Position.Z, enemy.Rotation,
                enemy.IsCasting, enemy.CastActionId, enemy.CastTotalSeconds, enemy.CastOmenDelay,
                enemy.CastTargetLocation?.X, enemy.CastTargetLocation?.Y, enemy.CastTargetLocation?.Z,
                castTargetEnemyNetId, castTargetRole,
                enemy.LastInstantCastSeq, enemy.LastInstantCastActionId,
                enemy.LastInstantCastTargetLocation?.X, enemy.LastInstantCastTargetLocation?.Y, enemy.LastInstantCastTargetLocation?.Z,
                instantTargetEnemyNetId, instantTargetRole));
        }

        var liveTethers = world.Children.OfType<SimTether>().Where(t => t.IsActive).ToList();
        foreach (var stale in hostTetherNetIds.Keys.Where(t => !liveTethers.Contains(t)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: tether NetId {hostTetherNetIds[stale]} no longer active -- dropping from broadcast.");
            hostTetherNetIds.Remove(stale);
        }

        var tethers = new List<TetherState>(liveTethers.Count);
        foreach (var tether in liveTethers)
        {
            var (aEnemy, aRole) = ResolveEnd(world, tether.A);
            var (bEnemy, bRole) = ResolveEnd(world, tether.B);
            if (!hostTetherNetIds.TryGetValue(tether, out var netId))
            {
                netId = nextTetherNetId++;
                hostTetherNetIds[tether] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new tether NetId {netId} (TetherId {tether.TetherId}) -- A={(aEnemy is { } ae ? $"enemy#{ae}" : aRole?.ToString() ?? "null")}, B={(bEnemy is { } be ? $"enemy#{be}" : bRole?.ToString() ?? "null")}.");
            }
            tethers.Add(new TetherState(netId, tether.TetherId, aEnemy, aRole, bEnemy, bRole));
        }

        var roles = new List<RoleState>(8);
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            var member = world.Party.Get(role);
            var dead = member is ISimPartyMember { Dead: true };
            IReadOnlyList<EnemyStatusState> statuses = [];
            if (member != null)
            {
                var statusSnapshot = member.ActiveStatusSnapshot;
                var statusKey = string.Join(",", statusSnapshot.Select(s => $"{s.StatusId}:{s.Stacks}"));
                if (!hostRoleLastLoggedStatuses.TryGetValue(role, out var lastStatusKey) || lastStatusKey != statusKey)
                {
                    hostRoleLastLoggedStatuses[role] = statusKey;
                    DiagnosticLog.Info($"[Multiplayer] Host: role {role} statuses -> [{statusKey}].");
                }
                if (member.LastLockonVfxId is { } lockonId
                    && (!hostRoleLastLoggedLockonVfx.TryGetValue(role, out var lastLockon) || lastLockon != lockonId))
                {
                    hostRoleLastLoggedLockonVfx[role] = lockonId;
                    DiagnosticLog.Info($"[Multiplayer] Host: role {role} LastLockonVfxId -> {lockonId}.");
                }
                statuses = statusSnapshot.Select(s => new EnemyStatusState(s.StatusId, s.Stacks)).ToList();
            }
            else
            {
                hostRoleLastLoggedStatuses.Remove(role);
                hostRoleLastLoggedLockonVfx.Remove(role);
            }
            roles.Add(new RoleState(role, member != null, dead,
                member?.Position.X ?? 0f, member?.Position.Y ?? 0f, member?.Position.Z ?? 0f, member?.Rotation ?? 0f,
                statuses, member?.LastLockonVfxId));
        }

        var liveEventObjects = world.Children.OfType<SimEventObject>().Where(o => o.IsActive).ToList();
        foreach (var stale in hostEventObjectNetIds.Keys.Where(o => !liveEventObjects.Contains(o)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: event object NetId {hostEventObjectNetIds[stale]} (EObj 0x{stale.EObjRowId:X}) no longer active -- dropping from broadcast.");
            hostEventObjectNetIds.Remove(stale);
        }

        var eventObjects = new List<EventObjectState>(liveEventObjects.Count);
        foreach (var eo in liveEventObjects)
        {
            if (!hostEventObjectNetIds.TryGetValue(eo, out var netId))
            {
                netId = nextEventObjectNetId++;
                hostEventObjectNetIds[eo] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new event object NetId {netId} -- EObj 0x{eo.EObjRowId:X}, pos {eo.Position}, state {eo.CurrentState}.");
            }
            eventObjects.Add(new EventObjectState(
                netId, eo.EObjRowId, eo.VisibleState, eo.CurrentState,
                eo.Position.X, eo.Position.Y, eo.Position.Z, eo.Rotation));
        }

        _ = relay!.SendAsync(new WorldSnapshotMessage(enemies, tethers, roles, eventObjects));
    }

    private (int? enemyNetId, PartyRole? role) ResolveEnd(SimWorld world, SimCharacter? c)
    {
        if (c is null) return (null, null);
        if (c is SimEnemy e) return hostEnemyNetIds.TryGetValue(e, out var id) ? (id, null) : (null, null);
        foreach (var role in Enum.GetValues<PartyRole>())
            if (ReferenceEquals(world.Party.Get(role), c)) return (null, role);
        return (null, null);
    }

    // Same job as ResolveEnd, but for a Cast() target: SimCast only ever stores the raw
    // GameObjectId it was given (see SimCast.TargetId's doc comment for why that number
    // means nothing to a peer on its own), so this resolves by ID equality against the
    // host's own local party/enemy set instead of ResolveEnd's reference equality.
    private (int? enemyNetId, PartyRole? role) ResolveTargetId(SimWorld world, GameObjectId? targetId)
    {
        if (targetId is not { } id) return (null, null);
        foreach (var role in Enum.GetValues<PartyRole>())
            if (world.Party.Get(role)?.GameObjectId == id) return (null, role);
        foreach (var (enemy, netId) in hostEnemyNetIds)
            if (enemy.GameObjectId == id) return (netId, null);
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
            {
                DiagnosticLog.Warn($"[Multiplayer] {Session.NameOf(peerId)} ({role}) hasn't reported in over {PeerStaleTimeoutMs / 1000}s -- likely disconnected.");
                // Mid-fight, a silently-vanished party member dooms the run the same
                // way an explicit "Leave session" click does (see RemovePeer's mid-
                // fight branch) -- but unlike that click, going stale isn't
                // necessarily permanent (a network blip, not a deliberate leave), so
                // only end the run here; their role claim and roster slot are left
                // alone so a reconnect (their own client already retries the same
                // relay/session automatically -- see BeginReconnect) drops them back
                // into a normal lobby instead of finding their role already handed
                // to someone else. IsInInstance guard: Leave() -> Unload() assumes a
                // zone was actually entered, same reasoning as everywhere else this
                // guard appears in this file.
                if (running && Plugin.GameInstance.World.Map.IsInInstance)
                {
                    DiagnosticLog.Info($"[Multiplayer] Ending the run because {Session.NameOf(peerId)} went stale mid-fight.");
                    Plugin.GameInstance.Leave();
                }
            }
            else if (!stale && warnedStalePeers.Remove(peerId))
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} ({role}) is reporting in again.");
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
        if (Session.RoleOf(msg.PeerId) is not { } role)
        {
            DiagnosticLog.Debug($"[Multiplayer] SelfPose from {msg.PeerId} but they hold no claimed role -- dropping.");
            return;
        }
        if (Plugin.GameInstance.World.Party.Get(role) is SimNetworkPuppet puppet)
            puppet.ApplyNetworkPose(new Vector3(msg.X, msg.Y, msg.Z), msg.Rotation);
        else
            DiagnosticLog.Debug($"[Multiplayer] SelfPose from {Session.NameOf(msg.PeerId)} ({role}) but that slot isn't a SimNetworkPuppet -- dropping.");
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
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of enemy NetId {e.NetId} -- BNpcBase {e.BNpcBaseId}, pos ({e.X:F2},{e.Y:F2},{e.Z:F2}), rot {e.Rotation:F2}, visible {e.Visible} -- spawning local doppel.");
                enemy = world.SpawnEnemy(config);
                if (enemy == null)
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SpawnEnemy returned null for NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) -- skipping this enemy.");
                    continue;
                }
                peerEnemies[e.NetId] = enemy;
            }
            // Smoothed in Tick rather than teleported here -- see SimEnemy.
            // ApplyNetworkPosition/TickNetworkPosition. A hard SetPosition every
            // snapshot made boss movement visibly stutter for peers; the first
            // snapshot's "spawn" branch above already places the doppel
            // exactly here via EnemySpawnConfig.Placement, so this call is a no-op
            // distance-wise on that first tick.
            enemy.ApplyNetworkPosition(new Vector3(e.X, e.Y, e.Z), e.Rotation);
            enemy.SetVisible(e.Visible);
            // Re-issued only on an actual change -- SetModelState's native rebuild
            // briefly disables/re-enables drawing, so calling it every snapshot
            // even when unchanged would flicker the model. A scenario's
            // mid-fight SetModelState calls (Kefka's grow transformation, Omega-M's
            // phase swaps, etc.) are otherwise a purely local Timeline write the
            // host never has any other reason to tell peers about -- without this a
            // peer's doppel just stays on whatever model it first spawned with.
            if (!peerEnemyModelState.TryGetValue(e.NetId, out var lastModelState) || lastModelState != e.ModelState)
            {
                peerEnemyModelState[e.NetId] = e.ModelState;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) ModelState -> 0x{e.ModelState:X2}.");
                enemy.SetModelState(e.ModelState);
            }
            // Reconciled against the host's set every snapshot -- AddStatus/RemoveStatus
            // are cheap direct StatusManager writes (no model rebuild involved), unlike
            // ModelState above, so there's no need to gate the actual application on a
            // change; only the log line below is edge-triggered. Without this, a
            // scenario's stack-based statuses (e.g. UMAD P3's "Max" grow status) never
            // reach a peer's doppel at all -- it stays Position/Visible/ModelState-correct
            // but visually un-grown.
            var currentStatuses = enemy.ActiveStatusSnapshot;
            foreach (var target in e.Statuses)
            {
                if (currentStatuses.Any(s => s.StatusId == target.StatusId && s.Stacks == target.Stacks)) continue;
                enemy.AddStatus(target.StatusId, stacks: target.Stacks, overrideStacks: true);
            }
            foreach (var current in currentStatuses)
            {
                if (e.Statuses.Any(s => s.StatusId == current.StatusId)) continue;
                enemy.RemoveStatus(current.StatusId);
            }
            var statusKey = string.Join(",", e.Statuses.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!peerEnemyLastLoggedStatuses.TryGetValue(e.NetId, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                peerEnemyLastLoggedStatuses[e.NetId] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) statuses -> [{statusKey}].");
            }
            // Rising-edge trigger: reuses the real SimCast pipeline (cast bar +
            // omen VFX) rather than faking either, so timing/placement come from
            // the same code path solo play already exercises. targetLocation is
            // threaded through for ground-targeted casts (e.g. BlizzardIII spread
            // markers) -- omitting it would anchor the telegraph at the caster's
            // own position instead of the intended ground spot (see NativeCast).
            // castSeconds is threaded through for the same reason: leaving it null
            // makes SimCast.Start fall back to a Lumina sheet lookup, which for a
            // scenario's synthetic helper-enemy action IDs either resolves to a
            // duration that has nothing to do with what the host scripted (the
            // telegraph runs too short, out of sync with the host's real damage
            // timing) or isn't in the sheet at all (Start() logs a warning and the
            // cast never begins on the peer -- the animation silently never plays).
            // omenDelay is threaded through too -- left at its 0f default, a cast
            // like Damning Edict (scripted with omenDelay: 4.1f so its telegraph
            // only shows for the last ~0.9s of a 5s cast) would instead show that
            // telegraph for the entire cast on a peer's screen.
            // targetId is resolved via ResolvePeerEnd (same helper tethers already
            // use) from the host's role/enemy-NetId translation of its own raw
            // GameObjectId -- see SimCast.TargetId's doc comment for why the ID
            // itself can't just cross the network. Omitting it entirely (as the
            // original TargetLocation-only fix did) left entity-targeted casts like
            // UMAD P3's Thunder III tankbuster with no target at all on a peer, so
            // NativeActionEffect's NumTargets went to 0 and the hit-react animation
            // that's supposed to play on the tank being hit never showed up there.
            if (e.IsCasting && !enemy.IsCasting)
            {
                var targetLocation = e.CastTargetX is { } tx && e.CastTargetY is { } ty && e.CastTargetZ is { } tz
                    ? new Vector3(tx, ty, tz)
                    : (Vector3?)null;
                var targetId = ResolvePeerEnd(world, e.CastTargetEnemyNetId, e.CastTargetRole)?.GameObjectId;
                enemy.Cast(e.CastActionId, targetLocation: targetLocation, castSeconds: e.CastSeconds, omenDelay: e.CastOmenDelay, targetId: targetId);
            }
            // Edge-triggered on a monotonic counter rather than IsCasting's rising
            // edge -- an instant cast (e.g. Nothingness) never makes IsCasting go
            // true at all (see SimCast.LastInstantCastSeq's doc comment), so this is
            // the only signal a peer has that one happened. Guarded on seq > 0 so a
            // peer that just connected doesn't replay the zero-value default the
            // instant it sees its first snapshot for this enemy.
            if (e.LastInstantCastSeq > 0
                && (!peerEnemyLastInstantCastSeq.TryGetValue(e.NetId, out var lastInstantSeq) || lastInstantSeq != e.LastInstantCastSeq))
            {
                peerEnemyLastInstantCastSeq[e.NetId] = e.LastInstantCastSeq;
                var instantTargetLocation = e.LastInstantCastTargetX is { } itx && e.LastInstantCastTargetY is { } ity && e.LastInstantCastTargetZ is { } itz
                    ? new Vector3(itx, ity, itz)
                    : (Vector3?)null;
                var instantTargetId = ResolvePeerEnd(world, e.LastInstantCastTargetEnemyNetId, e.LastInstantCastTargetRole)?.GameObjectId;
                enemy.Cast(e.LastInstantCastActionId, targetLocation: instantTargetLocation, castSeconds: 0f, targetId: instantTargetId);
            }
            // Edge-triggered like ModelState -- PlayAnimationTimeline/AttachLockonVfx
            // are one-shot cues, so re-issuing them every snapshot even when
            // unchanged would restart the same animation/VFX on a loop.
            if (e.AnimationTimelineId is { } timelineId
                && (!peerEnemyAnimationTimeline.TryGetValue(e.NetId, out var lastTimeline) || lastTimeline != timelineId))
            {
                peerEnemyAnimationTimeline[e.NetId] = timelineId;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) AnimationTimelineId -> 0x{timelineId:X4}.");
                enemy.PlayAnimationTimeline(timelineId);
            }
            if (e.LastLockonVfxId is { } lockonId
                && (!peerEnemyLastLockonVfx.TryGetValue(e.NetId, out var lastLockon) || lastLockon != lockonId))
            {
                peerEnemyLastLockonVfx[e.NetId] = lockonId;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) LastLockonVfxId -> {lockonId}.");
                enemy.AttachLockonVfx(lockonId, persistent: false);
            }
        }
        foreach (var staleId in peerEnemies.Keys.Where(id => !seenEnemyIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {staleId} no longer in snapshot -- despawning local doppel.");
            peerEnemies[staleId].Despawn();
            peerEnemies.Remove(staleId);
            peerEnemyModelState.Remove(staleId);
            peerEnemyLastLoggedStatuses.Remove(staleId);
            peerEnemyAnimationTimeline.Remove(staleId);
            peerEnemyLastLockonVfx.Remove(staleId);
            peerEnemyLastInstantCastSeq.Remove(staleId);
        }

        // UMAD P3 only (harmless no-op elsewhere -- no enemy will ever match
        // BlackHole's BNpcBaseId in another scenario). A peer never runs the
        // scenario's own Run_BlackHoleObstacles, so without this a debug-bot
        // peer's own MoveTo pathing has no avoidance data and can cut straight
        // through a black hole's damage radius mid-transit -- which the host
        // would then apply DamageDown for, since the peer's own reported
        // position is self-authoritative. Rebuilt from the already-synced
        // peerEnemies rather than replaying the scenario's RNG state, so it
        // can never drift from whatever the host is actually showing.
        world.Obstacles.Clear();
        // Diagnostic-only, paired with UmadP3BlackHoleScenario.Tick's own "Near black
        // hole" log: that one is the host's belief (built off this peer's last
        // self-reported pose, via SimNetworkPuppet.Position); this is the peer's own,
        // true local position at the same real moment. If a future DamageDown dump
        // shows the host's line but not this one at a comparable timestamp, the pose
        // report was stale when it mattered -- if both show it, the peer's own
        // pathing genuinely cut it close. localPlayer is null on the host (it drives
        // its own bots directly, no self-pose loop), so this is peer-only already.
        var localPlayer = Plugin.GameInstance.World.Party.Player;
        foreach (var (netId, bh) in peerEnemies.Where(kvp => kvp.Value.BNpcBaseId == BNpcBaseId.BlackHole))
        {
            world.Obstacles.Add(new CircleObstacle(new Vector2(bh.Position.X, bh.Position.Z), UmadP3BlackHoleScenario.BlackHoleAvoidRadius));
            if (localPlayer is null) continue;
            var distSq = localPlayer.Placement().DistanceSq(bh.Position);
            if (distSq < UmadP3BlackHoleScenario.NearBlackHoleLogRadius * UmadP3BlackHoleScenario.NearBlackHoleLogRadius)
                DiagnosticLog.Info(
                    $"[Multiplayer] Peer: local position ({localPlayer.Position.X:F2},{localPlayer.Position.Z:F2}) is {MathF.Sqrt(distSq):F2}y from black hole NetId {netId} at ({bh.Position.X:F2},{bh.Position.Z:F2}).");
        }

        // Debug-bot replay: Chaos/Exdeath might not have been replicated yet
        // when TryStartDebugBotReplay first resolved them (WorldSnapshot and
        // AiReplayStateMessage are independent flows) -- keep retrying here on
        // every snapshot until each is found, cheap once it's no longer null.
        if (debugShadowState is { } shadow)
        {
            shadow.ScenarioObjects.Chaos ??= peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.ChaosP3);
            shadow.ScenarioObjects.Exdeath ??= peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.Exdeath);
        }

        var seenTetherIds = new HashSet<int>();
        foreach (var t in snap.Tethers)
        {
            seenTetherIds.Add(t.NetId);
            var a = ResolvePeerEnd(world, t.AEnemyNetId, t.ARole);
            var b = ResolvePeerEnd(world, t.BEnemyNetId, t.BRole);
            if (a == null && b == null) continue;
            // Re-create on any endpoint change (e.g. a grabby tether's B going from
            // unattached to a role once someone actually grabs it) -- SimTether's
            // endpoints are fixed at construction, so there's no in-place update.
            // Without this, a peer's local tether object -- and anything reading it,
            // like UmadP3BlackHoleAi.PullTether's ReferenceEquals(t.B, player) check
            // -- stays frozen at whatever endpoints were true the instant this NetId
            // was first seen, for the rest of the run.
            var aDesc = t.AEnemyNetId is { } aId ? $"enemy#{aId}" : t.ARole?.ToString() ?? "null";
            var bDesc = t.BEnemyNetId is { } bId ? $"enemy#{bId}" : t.BRole?.ToString() ?? "null";
            if (peerTethers.TryGetValue(t.NetId, out var existing))
            {
                if (ReferenceEquals(existing.A, a) && ReferenceEquals(existing.B, b)) continue;
                DiagnosticLog.Info($"[Multiplayer] Peer: tether NetId {t.NetId} endpoint changed -- recreating (A={aDesc}, B={bDesc}).");
                existing.Despawn();
            }
            else
            {
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of tether NetId {t.NetId} (TetherId {t.TetherId}) -- A={aDesc}, B={bDesc}.");
            }
            peerTethers[t.NetId] = world.Tether(a, b, t.TetherId);
        }
        foreach (var staleId in peerTethers.Keys.Where(id => !seenTetherIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: tether NetId {staleId} no longer in snapshot -- despawning.");
            peerTethers[staleId].Despawn();
            peerTethers.Remove(staleId);
        }

        var myRole = MyClaimedRole;
        foreach (var r in snap.Roles)
        {
            // Position is self-authoritative for our own claimed role (a real
            // SimPlayer reports its own pose via SelfPoseMessage, never network-driven
            // here) -- but statuses/lockon VFX are NOT: we never run any scenario
            // logic ourselves, so nothing else would ever call AddStatus/
            // AttachLockonVfx against our own real character. Reconcile those for
            // every role, including our own.
            if (r.Role != myRole && world.Party.Get(r.Role) is SimNetworkPuppet puppet)
                puppet.ApplyNetworkPose(new Vector3(r.X, r.Y, r.Z), r.Rotation);

            if (world.Party.Get(r.Role) is not { } member) continue;
            var currentStatuses = member.ActiveStatusSnapshot;
            if (!peerRoleReconciledStatusIds.TryGetValue(r.Role, out var reconciledIds))
                peerRoleReconciledStatusIds[r.Role] = reconciledIds = new HashSet<ushort>();
            foreach (var target in r.Statuses)
            {
                // Tracked regardless of whether AddStatus actually needs to run this
                // tick, so the removal loop below recognizes it as host-managed even
                // on a snapshot where nothing changed.
                reconciledIds.Add(target.StatusId);
                if (currentStatuses.Any(s => s.StatusId == target.StatusId && s.Stacks == target.Stacks)) continue;
                member.AddStatus(target.StatusId, stacks: target.Stacks, overrideStacks: true);
            }
            // Only ever removes a statusId THIS reconciliation previously added --
            // never diffs against the character's full ActiveStatusSnapshot the way
            // the enemy path does, since a peer's own real character can carry
            // statuses nothing here put there (see peerRoleReconciledStatusIds).
            foreach (var trackedId in reconciledIds.ToList())
            {
                if (r.Statuses.Any(s => s.StatusId == trackedId)) continue;
                member.RemoveStatus(trackedId);
                reconciledIds.Remove(trackedId);
            }
            var statusKey = string.Join(",", r.Statuses.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!peerRoleLastLoggedStatuses.TryGetValue(r.Role, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                peerRoleLastLoggedStatuses[r.Role] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Peer: role {r.Role} statuses -> [{statusKey}].");
            }
            if (r.LastLockonVfxId is { } lockonId
                && (!peerRoleLastLockonVfx.TryGetValue(r.Role, out var lastLockon) || lastLockon != lockonId))
            {
                peerRoleLastLockonVfx[r.Role] = lockonId;
                DiagnosticLog.Info($"[Multiplayer] Peer: role {r.Role} LastLockonVfxId -> {lockonId}.");
                member.AttachLockonVfx(lockonId, persistent: false);
            }
        }

        var seenEventObjectIds = new HashSet<int>();
        foreach (var o in snap.EventObjects)
        {
            seenEventObjectIds.Add(o.NetId);
            if (!peerEventObjects.TryGetValue(o.NetId, out var eo))
            {
                var config = new EventObjectSpawnConfig
                {
                    EObjId = o.EObjId,
                    Placement = new Placement(new Vector3(o.X, o.Y, o.Z), o.Rotation),
                    TimelineState = o.TimelineState,
                    SpawnVisible = true,
                };
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of event object NetId {o.NetId} -- EObj 0x{o.EObjId:X}, pos ({o.X:F2},{o.Y:F2},{o.Z:F2}), state {o.CurrentState} -- spawning local copy.");
                eo = world.SpawnEventObject(config);
                if (eo == null)
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SpawnEventObject returned null for NetId {o.NetId} (EObj 0x{o.EObjId:X}) -- skipping.");
                    continue;
                }
                peerEventObjects[o.NetId] = eo;
            }
            eo.SetPosition(new Placement(new Vector3(o.X, o.Y, o.Z), o.Rotation));
            // Edge-triggered like ModelState -- SetState is a plain field write with
            // no rebuild to worry about, but re-issuing it every snapshot even
            // when unchanged is pointless churn.
            if (!peerEventObjectState.TryGetValue(o.NetId, out var lastState) || lastState != o.CurrentState)
            {
                peerEventObjectState[o.NetId] = o.CurrentState;
                DiagnosticLog.Info($"[Multiplayer] Peer: event object NetId {o.NetId} (EObj 0x{o.EObjId:X}) CurrentState -> {o.CurrentState}.");
                eo.SetState(o.CurrentState);
            }
        }
        foreach (var staleId in peerEventObjects.Keys.Where(id => !seenEventObjectIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: event object NetId {staleId} no longer in snapshot -- despawning local copy.");
            peerEventObjects[staleId].Despawn();
            peerEventObjects.Remove(staleId);
            peerEventObjectState.Remove(staleId);
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
        DiagnosticLog.Info($"[Multiplayer] {msg.Role} killed: {msg.Cause}");
        if (Plugin.GameInstance.World.Party.Get(msg.Role) is ISimPartyMember member)
            Plugin.GameInstance.Kill(member, msg.Cause);
        else
            DiagnosticLog.Debug($"[Multiplayer] RoleKilled for {msg.Role} but that slot isn't an ISimPartyMember locally -- dropping.");
    }

    private void OnEndReceived(EndMessage msg)
    {
        if (IsHost || !running) return;
        DiagnosticLog.Info($"[Multiplayer] Peer received EndMessage (ReturnedToInn={msg.ReturnedToInn}).");
        running = false;
        StopDebugBotReplay();
        // If our own deferred zone entry (RunScenarioAsPeer) hasn't actually
        // completed yet, there's nothing to leave or reset -- and calling
        // Leave() here specifically would be actively harmful: Unload()
        // assumes a zone was entered (it restores the real character to the
        // position ZoneSession.Enter() saved) and would instead teleport them
        // to garbage coordinates. Reset() has no equivalent issue (peers never
        // set ActiveScenario, so its own teleport-back-if-needed check never
        // fires), but skip it too here for symmetry -- there's truly nothing
        // to reset.
        if (!Plugin.GameInstance.World.Map.IsInInstance)
        {
            DiagnosticLog.Info("[Multiplayer] EndMessage received before our own deferred zone entry completed -- nothing to leave/reset.");
            return;
        }
        // Mirror whichever the host actually did -- Leave() if they left the
        // zone entirely, or Reset() to match them staying in-zone (ready for a
        // quick re-Start) instead of always hard-kicking to the inn.
        if (msg.ReturnedToInn)
            Plugin.GameInstance.Leave();
        else
            Plugin.GameInstance.Reset();
    }

    // ---- Debug: bot-controlled peer replay ----------------------------------

    // Host-only, edge-triggered once per run. LastState isn't guaranteed set on
    // the very first Tick() after StartScenario -- RunScenarioAsHost's actual
    // work (including constructing the scenario's *State) is deferred via
    // Plugin.Framework.Run, same reasoning as peerEnteredInstance below -- so
    // this polls instead of reading it synchronously right after the call.
    // Sent unconditionally: the host never knows or cares which peers, if any,
    // are using it locally. One case per multiplayer-supported scenario (see
    // SupportedScenarios) -- add a new one here alongside a new *AiReplayStateMessage
    // and *State.FromNetworkReplay when porting another scenario.
    private void TrySendAiReplayState()
    {
        switch (Plugin.GameInstance.Scenarios[Session.ScenarioIndex])
        {
            case UmadP3BlackHoleScenario p3Scenario:
                if (p3Scenario.LastState is not { } p3State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new AiReplayStateMessage(
                    p3State.Roles.List, p3State.StackTargets.List, p3State.SlapAttacks.ToArray(),
                    p3State.KefkaPosition.Select(d => d.RadiansFromNorth).ToArray(), p3State.ImplosionAttack));
                break;
            case UmadP2ForsakenScenario p2Scenario:
                if (p2Scenario.LastState is not { } p2State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P2AiReplayStateMessage(
                    p2State.EndAttacks, p2State.NewNorth.RadiansFromNorth, p2State.Rotation, p2State.Lockons));
                break;
            case UmadP4KefkaSaysScenario p4Scenario:
                if (p4Scenario.LastState is not { } p4State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P4AiReplayStateMessage(
                    p4State.Mystery.Select(m => m.BlizzardOffset).ToArray(),
                    p4State.Mystery.Select(m => m.LightningOffset).ToArray(),
                    p4State.Mystery.Select(m => m.LightningOrientation).ToArray(),
                    p4State.Wave1First, p4State.Wave1.List, p4State.Wave1True,
                    p4State.Wave2.List, p4State.Wave2True,
                    p4State.InfernoMystery.IsTrue, p4State.TsunamiMystery.IsTrue,
                    p4State.Wave3.List, p4State.Wounds,
                    p4State.Antilights[0].Antilight == Antilight.White,
                    p4State.NeoExdeathDirection.RadiansFromNorth));
                break;
            case UmadP5ExaflaresScenario p5Scenario:
                if (p5Scenario.LastState is not { } p5State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P5AiReplayStateMessage(p5State.LeftOrder.ToArray(), p5State.RightOrder.ToArray()));
                break;
        }
    }

    // Guards the AiStrats[Session.SelectedAi] indexing every multi-strat
    // scenario's replay branch does below. StartScenario/MultiplayerWindow
    // already refuse to broadcast an out-of-range SelectedAi (see
    // MainWindow.HasStartableStrat), but a peer has no independent way to
    // verify what the host sent, so this is the last line of defense against
    // an IndexOutOfRangeException here.
    private bool IsValidAiIndex(IScenario scenario) =>
        Session.SelectedAi >= 0 && Session.SelectedAi < scenario.AiStrats.Count;

    // Peer-only, idempotent, edge-triggered once per run: fires once both the
    // host's *AiReplayStateMessage has arrived (pending*AiReplayState) and our
    // own zone/party is actually ready (peerEnteredInstance) -- arrival order
    // between those two isn't guaranteed, so both call sites (Dispatch and
    // Tick) funnel through here. A no-op entirely unless debug-bot mode is on.
    // Branches on the active scenario the same way TrySendAiReplayState does.
    private void TryStartDebugBotReplay()
    {
        if (!debugBotControlled || debugBotReplayStarted) return;
        if (!peerEnteredInstance) return;
        if (MyClaimedRole is not { } myRole) return;
        var world = Plugin.GameInstance.World;

        switch (Plugin.GameInstance.Scenarios[Session.ScenarioIndex])
        {
            case UmadP3BlackHoleScenario when pendingAiReplayState is { } msg:
            {
                debugBotReplayStarted = true;
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP3BlackHoleState.FromNetworkReplay(
                    world, msg.Roles, msg.StackTargets, msg.SlapAttacks, msg.KefkaPositionRadians, msg.ImplosionAttack);
                // Chaos/Exdeath might not have been replicated yet (WorldSnapshot
                // and this message are independent flows) -- OnWorldSnapshotReceived
                // keeps retrying this resolution against debugShadowState below as
                // new enemies come in, so a still-null boss here isn't a lost
                // cause, just not needed until each choreography step that reads
                // it, tens of seconds into the fight at the earliest.
                shadowState.ScenarioObjects.Chaos = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.ChaosP3);
                shadowState.ScenarioObjects.Exdeath = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.Exdeath);
                debugShadowState = shadowState;
                DebugBotControl.Enabled = true;
                new UmadP3BlackHoleAi().Run(shadowState, world);
                break;
            }
            case UmadP2ForsakenScenario p2Scenario when pendingP2AiReplayState is { } p2Msg:
            {
                // Belt-and-suspenders: StartScenario/MultiplayerWindow already refuse
                // to broadcast an out-of-range SelectedAi (see HasStartableStrat), but
                // marking replay "started" either way (rather than just `break`) stops
                // this from silently re-attempting -- and re-logging nothing -- every
                // single frame if it's ever somehow reached anyway.
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p2Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p2Scenario.Name} ({p2Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP2ForsakenState.FromNetworkReplay(
                    p2Msg.EndAttacks, p2Msg.NewNorthRadians, p2Msg.Rotation, p2Msg.Lockons);
                debugShadowStateP2 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP2ForsakenState>)p2Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
            case UmadP4KefkaSaysScenario p4Scenario when pendingP4AiReplayState is { } p4Msg:
            {
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p4Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p4Scenario.Name} ({p4Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP4KefkaSaysState.FromNetworkReplay(
                    world.Party, p4Msg.MysteryBlizzardOffset, p4Msg.MysteryLightningOffset, p4Msg.MysteryLightningOrientation,
                    p4Msg.Wave1First, p4Msg.Wave1, p4Msg.Wave1True, p4Msg.Wave2, p4Msg.Wave2True,
                    p4Msg.InfernoIsTrue, p4Msg.TsunamiIsTrue, p4Msg.Wave3, p4Msg.Wounds,
                    p4Msg.Antilight0IsWhite, p4Msg.NeoExdeathDirectionRadians);
                debugShadowStateP4 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP4KefkaSaysState>)p4Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
            case UmadP5ExaflaresScenario p5Scenario when pendingP5AiReplayState is { } p5Msg:
            {
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p5Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p5Scenario.Name} ({p5Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                // A fresh, peer-owned EventScheduler -- NOT the host's -- since
                // UmadP5ExaflaresAi schedules its dodges directly onto it, and
                // nothing would ever drive one shared with anything else. See
                // MultiplayerManager.Tick's P5-specific branch just below for
                // what actually ticks it every frame (mirrors UmadP5ExaflaresScenario.Tick,
                // which normally does this but never runs on a peer).
                var shadowState = UmadP5ExaflaresState.FromNetworkReplay(p5Msg.LeftOrder, p5Msg.RightOrder, new EventScheduler());
                debugShadowStateP5 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP5ExaflaresState>)p5Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
        }
    }

    // Clears just the current run's replay state, not the debugBotControlled
    // toggle itself (a sticky lobby preference) -- called whenever running
    // stops for any reason, so a debug-bot peer's real character always
    // regains normal control the instant the fight ends rather than staying
    // bot-driven while standing in an empty arena or back in the inn.
    private void StopDebugBotReplay()
    {
        if (debugBotReplayStarted) DiagnosticLog.Info("[Multiplayer] Peer: stopping debug-bot replay.");
        DebugBotControl.Enabled = false;
        pendingAiReplayState = null;
        pendingP2AiReplayState = null;
        pendingP4AiReplayState = null;
        pendingP5AiReplayState = null;
        debugShadowState = null;
        debugShadowStateP2 = null;
        debugShadowStateP4 = null;
        debugShadowStateP5 = null;
        debugBotReplayStarted = false;
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
            disconnectedSinceMs ??= Environment.TickCount64;
            if (failure != null)
            {
                DiagnosticLog.Warn($"[Multiplayer] Disconnected: {failure.Message}");
                ConnectionError = failure.Message;
            }
            else
            {
                DiagnosticLog.Info("[Multiplayer] Disconnected (no failure reported -- socket just closed).");
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
            DiagnosticLog.Warn($"[Multiplayer] Error handling {message.GetType().Name}: {e}");
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
        // send it). When it *is* the host leaving, it's moot anyway since
        // receiving it tears the whole session down a few lines later
        // regardless; when it's a departing peer instead, it plainly isn't a
        // host message at all and must not be mistaken for one.
        if (!IsHost && message is LobbyStateMessage or StartMessage or WorldSnapshotMessage
            or RoleKilledMessage or EndMessage or PingMessage or PeerStatusMessage
            or AiReplayStateMessage or P2AiReplayStateMessage or P4AiReplayStateMessage or P5AiReplayStateMessage)
            lastHostMessageMs = Environment.TickCount64;

        switch (message)
        {
            // Host-authoritative: only the host acts on requests other clients send.
            case HelloMessage hello when IsHost:
                peerLastSeenMs[hello.PeerId] = Environment.TickCount64;
                Session.Names[hello.PeerId] = hello.DisplayName;
                Session.Builds[hello.PeerId] = new PeerBuildInfo(hello.Version, hello.Checksum);
                DiagnosticLog.Info($"[Multiplayer] Hello from {hello.PeerId} ({hello.DisplayName}), build {hello.Version} ({new PeerBuildInfo(hello.Version, hello.Checksum).ShortChecksum}), mismatch={IsVersionMismatched(hello.PeerId)}.");
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
            case StartCheckMessage when !IsHost:
            {
                var reason = CheckOwnStartReadiness();
                _ = relay?.SendAsync(new StartCheckResponseMessage(MyPeerId, reason == null, reason));
                break;
            }
            case StartCheckResponseMessage resp when IsHost:
                DiagnosticLog.Info($"[Multiplayer] StartCheck reply from {Session.NameOf(resp.PeerId)}: ready={resp.Ready}{(resp.Reason is { } r ? $" ({r})" : "")}.");
                // Remove(...) returning false means either a duplicate/stale
                // reply or one that arrived after the timeout already gave up
                // on this peer -- either way there's nothing left to do with it.
                if (pendingStartResponses == null || !pendingStartResponses.Remove(resp.PeerId)) break;
                if (!resp.Ready) startCheckFailures[resp.PeerId] = resp.Reason ?? "not ready";
                if (pendingStartResponses.Count == 0) FinishStartCheck();
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
            // No `when !IsHost` guard -- when the HOST leaves it ends the
            // session for the whole group (including any other peers), so
            // that branch has to run regardless of the recipient's role. A
            // departing peer, by contrast, only ever shrinks the roster (see
            // RemovePeer) -- the rest of the group keeps going.
            case SessionEndedMessage ended when ended.PeerId == Session.HostId:
            {
                // Read the sender's name before LeaveSessionInternal wipes Session out from under it.
                var who = Session.NameOf(ended.PeerId);
                DiagnosticLog.Info($"[Multiplayer] Host {who} left -- session ending for the whole group.");
                // IsInInstance guard: Leave() -> Unload() assumes a zone was
                // actually entered (it restores the real character to the
                // position ZoneSession.Enter() saved); running can briefly be
                // true before a peer's deferred zone entry actually completes
                // (and, on the host side, before RunScenarioAsHost's deferred
                // work finishes), and calling it too early teleports the real
                // character to garbage coordinates instead of reverting them
                // cleanly.
                if (running && Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = $"{who} left -- session ended.";
                LobbyChanged?.Invoke();
                break;
            }
            case SessionEndedMessage ended when IsHost:
                RemovePeer(ended.PeerId);
                break;
            case ResetRequestMessage req when IsHost:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested a reset.");
                Plugin.GameInstance.Reset();
                break;
            // IsInInstance guard (not running -- Leave() must still work after
            // a Reset, which clears running while leaving the group stuck
            // in-instance with nothing to show for it and no way back short of
            // disbanding the whole session): Leave() -> Unload() assumes a zone
            // was actually entered (it restores the real character to the
            // position ZoneSession.Enter() saved), and IsInInstance is only
            // ever set true once that has genuinely happened (MapController.
            // TryLoad), so it alone is the correct signal here.
            case LeaveRequestMessage req when IsHost:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested to leave the instance.");
                if (Plugin.GameInstance.World.Map.IsInInstance)
                    Plugin.GameInstance.Leave();
                break;
            case AiReplayStateMessage state when !IsHost:
                pendingAiReplayState = state;
                TryStartDebugBotReplay();
                break;
            case P2AiReplayStateMessage p2State when !IsHost:
                pendingP2AiReplayState = p2State;
                TryStartDebugBotReplay();
                break;
            case P4AiReplayStateMessage p4State when !IsHost:
                pendingP4AiReplayState = p4State;
                TryStartDebugBotReplay();
                break;
            case P5AiReplayStateMessage p5State when !IsHost:
                pendingP5AiReplayState = p5State;
                TryStartDebugBotReplay();
                break;
        }
    }
}
