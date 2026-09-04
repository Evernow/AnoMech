using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

// Lobby roster, mirrored on every client from the host's LobbyStateMessage broadcasts. The
// host owns the authoritative copy; this class is a plain data holder, not a source of truth.
public sealed class MultiplayerSession
{
    public Guid HostId { get; set; }
    public Dictionary<PartyRole, Guid> ClaimedBy { get; private set; } = new();
    public Dictionary<Guid, string> Names { get; private set; } = new();
    public Dictionary<Guid, PeerBuildInfo> Builds { get; private set; } = new();
    public bool Started { get; set; }

    // Indices into Game.Scenarios and that scenario's own AiStrats/WaymarkPresets, set by the
    // host in StartScenario so every client resolves the same scenario/strat/waymark.
    public int ScenarioIndex { get; set; }
    public int SelectedAi { get; set; }
    public int SelectedWaymark { get; set; }

    // Host's pre-fight tankbuster mitigation plan, keyed by TankBusterCastInfo.Id (see
    // Scenarios/TankMitigation.cs). 0/missing = no mitigation planned for that cast --
    // a bot-driven tank with no plan entry just takes the hit unmitigated.
    public Dictionary<string, ushort> TankBusterPlan { get; private set; } = new();

    public void ApplyLobbyState(LobbyStateMessage msg)
    {
        HostId = msg.HostId;
        ClaimedBy = new Dictionary<PartyRole, Guid>(msg.ClaimedBy);
        Names = new Dictionary<Guid, string>(msg.Names);
        Builds = new Dictionary<Guid, PeerBuildInfo>(msg.Builds);
        Started = msg.Started;
        ScenarioIndex = msg.ScenarioIndex;
        SelectedAi = msg.SelectedAi;
        SelectedWaymark = msg.SelectedWaymark;
        TankBusterPlan = new Dictionary<string, ushort>(msg.TankBusterPlan);
    }

    public LobbyStateMessage ToMessage() => new(
        HostId, new Dictionary<PartyRole, Guid>(ClaimedBy), new Dictionary<Guid, string>(Names),
        new Dictionary<Guid, PeerBuildInfo>(Builds), Started, ScenarioIndex, SelectedAi, SelectedWaymark,
        new Dictionary<string, ushort>(TankBusterPlan));

    public PartyRole? RoleOf(Guid peerId) =>
        ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => (PartyRole?)kv.Key).FirstOrDefault();

    public string NameOf(Guid peerId) => Names.GetValueOrDefault(peerId, "Player");
}
