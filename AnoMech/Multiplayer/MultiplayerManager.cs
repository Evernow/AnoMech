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
public sealed partial class MultiplayerManager : IDisposable
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    // Mirrors UmadP5ExaflaresScenario.FrameGapCapSeconds -- a peer's P5 debug-bot
    // replay ticks debugShadowStateP5.Timeline off this Tick's own deltaSeconds
    // (see Tick()'s peer branch), which has no equivalent hitch guard of its own;
    // without this, a pause/alt-tab/loading-stall frame would fire every event
    // still queued on that timeline at once instead of skipping the frame like
    // the real scenario's wall-clock Stopwatch does.
    private const float P5ReplayFrameGapCapSeconds = 0.25f;

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
    // PlayAnimationTimeline calls. Absent from the dictionary == "never
    // logged yet" (distinct from "logged 0"), since 0 is a meaningful
    // default-y value for the field. AttachLockonVfx no longer needs an
    // edge-triggered dictionary of its own -- see SimCharacter.
    // DrainPendingLockonVfxIds's doc comment: every call since the last drain
    // is inherently new, so there's nothing to compare against.
    private readonly Dictionary<SimEnemy, ushort> hostEnemyLastLoggedAnimationTimeline = new();
    // Host-only: same edge-triggered-logging-only purpose, for party-role
    // AddStatus/RemoveStatus calls -- keyed by PartyRole (fixed 8-entry set)
    // rather than a Dictionary<SimCharacter,...>, since whichever SimCharacter
    // occupies a role changes across a claim/release but the role itself doesn't.
    private readonly Dictionary<PartyRole, string> hostRoleLastLoggedStatuses = new();
    // Host-only, edge-triggered against UmadP2ForsakenState.Lockons -- see
    // P2LockonsUpdateMessage's doc comment for why this needs its own re-syncable
    // channel instead of the one-time P2AiReplayStateMessage snapshot.
    private string? hostLastBroadcastP2Lockons;

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
    // Peer-only: last-applied AnimationTimelineId per enemy NetId,
    // edge-triggered like peerEnemyModelState -- re-issuing
    // PlayAnimationTimeline every snapshot even when unchanged would restart
    // the same animation on a loop. EnemyState.NewLockonVfxIds needs no
    // equivalent tracking: it's already a drained since-last-snapshot list
    // (see SimCharacter.DrainPendingLockonVfxIds), so every id it carries is
    // inherently new and gets applied unconditionally below.
    private readonly Dictionary<int, ushort> peerEnemyAnimationTimeline = new();
    // Peer-only: last-seen LastInstantCastSeq per enemy NetId, edge-triggered the
    // same way -- see EnemyState.LastInstantCastSeq's doc comment for why instant
    // casts need this separate counter instead of the IsCasting rising edge.
    private readonly Dictionary<int, int> peerEnemyLastInstantCastSeq = new();
    // Peer-only: last-seen CastSeq per enemy NetId -- see EnemyState.CastSeq's doc
    // comment for why a telegraphed cast's replay dedupes off this instead of the
    // IsCasting rising edge alone.
    private readonly Dictionary<int, int> peerEnemyLastCastSeq = new();
    // Peer-only: last-applied CurrentState per event-object NetId, edge-triggered
    // the same way -- SetState is a plain field write with no native rebuild to
    // worry about, but re-issuing it unconditionally every snapshot is still
    // pointless churn once it's already correct.
    private readonly Dictionary<int, ushort> peerEventObjectState = new();
    // Peer-only role equivalent of peerEnemyLastLoggedStatuses above -- keyed
    // by PartyRole like its host-side counterpart. Reconciled/applied for
    // every role INCLUDING the peer's own claimed one: unlike position
    // (self-authoritative -- see OnWorldSnapshotReceived), a peer never runs
    // any scenario logic themselves, so nothing else would ever call
    // AddStatus/AttachLockonVfx against their own real character.
    private readonly Dictionary<PartyRole, string> peerRoleLastLoggedStatuses = new();
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
    // Distinct, shorter timeout for "never heard from a host at all since joining" --
    // used to detect a mistyped/nonexistent session code. The relay is a dumb
    // broadcast room keyed purely by code (see Relay/AnoMech.Relay/Program.cs's
    // TryJoin): it auto-creates an empty room for any code, so there is no
    // "session not found" at the transport level -- a bad code just looks like an
    // empty room nobody else ever joins. PeerStaleTimeoutMs is tuned for the much
    // worse case of a host that WAS present going silent mid-fight, hence the
    // longer grace; this case has no established connection to preserve, so it can
    // fail fast.
    private const long NoHostFoundTimeoutMs = 4000;
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
    // True once any actual host-broadcast message has ever been received this
    // session (set in DispatchCore) -- distinguishes "never heard from a host"
    // (IsSessionNotFound) from "was hearing from one, then stopped" (IsHostStale).
    // lastHostMessageMs alone can't make that distinction: it's seeded to "now" on
    // every join/reconnect specifically so it doesn't read as decades-stale before
    // the first real message, which also means it can't tell a fresh join apart
    // from a session that's actually been silent the whole time.
    private bool everHeardFromHost;

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

    // Host-only: BroadcastRunEnded's EndMessage is fire-and-forget over a possibly-bad
    // connection -- if it's lost, a peer has no way to find out the run ended short
    // of PeerStaleTimeoutMs, or ever. Non-null while resends are still owed; see Tick().
    private const int EndMessageResendCount = 4;
    private const float EndMessageResendIntervalSeconds = 1f;
    private bool? pendingEndResendReturnedToInn;
    private int endResendsRemaining;
    private float endResendTimer;

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
    // scenario's shadow-state shape/fields differ.
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
    // Lets MultiplayerWindow hold a peer on a "connecting" screen instead of the
    // full role-list lobby until a host is actually confirmed present -- SessionCode
    // alone (what Draw() used to gate on) is set synchronously on JoinSession, well
    // before any confirmation a host exists on the other end of that code.
    public bool EverHeardFromHost => everHeardFromHost;
    // everHeardFromHost required so this can't overlap with IsSessionNotFound below --
    // a host that goes silent before ever sending anything is a nonexistent/mistyped
    // session, not a stale one.
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
        everHeardFromHost = false;

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
            || !selectedScenario.SupportsMultiplayer)
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

}
