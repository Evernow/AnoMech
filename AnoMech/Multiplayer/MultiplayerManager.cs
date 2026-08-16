using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    public MultiplayerSession Session { get; private set; } = new();
    public Guid MyPeerId { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsConnected => relay?.IsConnected ?? false;
    public bool IsRunning => running;
    public string? SessionCode { get; private set; }
    public string? RelayUrl { get; private set; }
    public string DisplayName { get; set; } = "Player";

    public PartyRole? MyClaimedRole => Session.RoleOf(MyPeerId);

    public event Action? LobbyChanged;

    // ---- Session lifecycle ----------------------------------------------

    public void HostSession(string relayUrl)
    {
        LeaveSession();
        MyPeerId = Guid.NewGuid();
        IsHost = true;
        RelayUrl = relayUrl;
        SessionCode = GenerateCode();
        Session = new MultiplayerSession { HostId = MyPeerId };
        Session.Names[MyPeerId] = DisplayName;

        relay = new RelayClient();
        relay.MessageReceived += OnMessageReceivedOffThread;
        relay.Disconnected += OnDisconnectedOffThread;
        _ = relay.ConnectAsync(relayUrl, SessionCode);
        LobbyChanged?.Invoke();
    }

    public void JoinSession(string relayUrl, string code)
    {
        LeaveSession();
        MyPeerId = Guid.NewGuid();
        IsHost = false;
        RelayUrl = relayUrl;
        SessionCode = code.Trim().ToUpperInvariant();
        Session = new MultiplayerSession();

        relay = new RelayClient();
        relay.MessageReceived += OnMessageReceivedOffThread;
        relay.Disconnected += OnDisconnectedOffThread;
        _ = ConnectAndHelloAsync(relayUrl, SessionCode);
    }

    private async System.Threading.Tasks.Task ConnectAndHelloAsync(string relayUrl, string code)
    {
        await relay!.ConnectAsync(relayUrl, code);
        await relay.SendAsync(new HelloMessage(MyPeerId, DisplayName));
    }

    public void LeaveSession()
    {
        if (IsHost) Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
        relay?.Dispose();
        relay = null;
        running = false;
        SessionCode = null;
        RelayUrl = null;
        Session = new MultiplayerSession();
        hostEnemyNetIds.Clear();
        hostTetherNetIds.Clear();
        peerEnemies.Clear();
        peerTethers.Clear();
    }

    public void Dispose() => LeaveSession();

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

    private void ApplyClaim(Guid peerId, PartyRole role)
    {
        if (Session.ClaimedBy.TryGetValue(role, out var holder) && holder != peerId) return; // already taken
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.ClaimedBy[role] = peerId;
        BroadcastLobbyState();
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
        if (MyClaimedRole is not { } myRole)
        {
            Plugin.Log.Warning("[Multiplayer] Cannot start: host has not claimed a role.");
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
        Plugin.GameInstance.PartyMemberKilled += OnPartyMemberKilledHost;
        Plugin.GameInstance.RunScenarioAsHost(scenario, myRole, selectedAi: 0, selectedWaymark: 0, networkRoles);
        running = true;
        LobbyChanged?.Invoke();
    }

    private void OnStartReceived()
    {
        if (IsHost) return;
        if (MyClaimedRole is not { } myRole)
        {
            Plugin.Log.Warning("[Multiplayer] Host started the scenario, but I never claimed a role -- ignoring.");
            return;
        }

        var scenario = Plugin.GameInstance.Scenarios.OfType<UmadP3BlackHoleScenario>().First();
        var networkRoles = Enum.GetValues<PartyRole>().Where(r => r != myRole).ToHashSet();

        peerEnemies.Clear();
        peerTethers.Clear();
        Plugin.GameInstance.RunScenarioAsPeer(scenario, myRole, selectedWaymark: 0, networkRoles);
        running = true;
    }

    // ---- Per-frame tick (framework thread; see Plugin.OnFrameworkUpdate) ----

    public void Tick(float deltaSeconds)
    {
        if (relay is not { IsConnected: true } || !running) return;

        if (IsHost)
        {
            // Reset/Leave clears ActiveScenario -- stop broadcasting once the local
            // run has ended rather than spamming empty snapshots (or, worse, a
            // later unrelated solo run) to peers who are still connected.
            if (Plugin.GameInstance.ActiveScenario == null)
            {
                running = false;
                Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
                return;
            }
            snapshotTimer += deltaSeconds;
            if (snapshotTimer < SnapshotIntervalSeconds) return;
            snapshotTimer = 0f;
            SampleAndBroadcastSnapshot();
        }
        else
        {
            if (!Plugin.GameInstance.World.Map.IsInInstance)
            {
                running = false;
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

    // ---- Message pump -------------------------------------------------------

    private void OnMessageReceivedOffThread(MpMessage message)
        => Plugin.Framework.Run(() => Dispatch(message));

    private void OnDisconnectedOffThread(Exception? failure)
        => Plugin.Framework.Run(() =>
        {
            if (failure != null) Plugin.Log.Warning($"[Multiplayer] Disconnected: {failure.Message}");
            LobbyChanged?.Invoke();
        });

    private void Dispatch(MpMessage message)
    {
        switch (message)
        {
            // Host-authoritative: only the host acts on requests other clients send.
            case HelloMessage hello when IsHost:
                Session.Names[hello.PeerId] = hello.DisplayName;
                BroadcastLobbyState();
                break;
            case ClaimRoleMessage claim when IsHost:
                ApplyClaim(claim.PeerId, claim.Role);
                break;
            case ReleaseRoleMessage release when IsHost:
                ApplyRelease(release.PeerId);
                break;
            case SelfPoseMessage pose when IsHost:
                OnSelfPoseReceived(pose);
                break;

            // Peer-facing broadcasts from the host.
            case LobbyStateMessage lobby when !IsHost:
                Session.ApplyLobbyState(lobby);
                LobbyChanged?.Invoke();
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
        }
    }
}
