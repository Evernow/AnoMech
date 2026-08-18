using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;

namespace AnoMech.Multiplayer;

// Lobby roster, mirrored on every client from the host's LobbyStateMessage
// broadcasts. Host also owns the authoritative copy it mutates directly before
// broadcasting (see MultiplayerManager) -- this class is a plain data holder,
// not itself a source of truth.
public sealed class MultiplayerSession
{
    public Guid HostId { get; set; }
    public Dictionary<PartyRole, Guid> ClaimedBy { get; private set; } = new();
    public Dictionary<Guid, string> Names { get; private set; } = new();
    public Dictionary<Guid, PeerBuildInfo> Builds { get; private set; } = new();
    public bool Started { get; set; }

    // Which scenario/strat/waymark the host is running (or about to run) --
    // indices into Game.Scenarios and that scenario's own AiStrats/WaymarkPresets.
    // Set by the host in MultiplayerManager.StartScenario, read by OnStartReceived
    // on every client (including the host's own copy of Session) so everyone
    // resolves the identical scenario/strat/waymark. See LobbyStateMessage.
    public int ScenarioIndex { get; set; }
    public int SelectedAi { get; set; }
    public int SelectedWaymark { get; set; }

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
    }

    public LobbyStateMessage ToMessage() => new(
        HostId, new Dictionary<PartyRole, Guid>(ClaimedBy), new Dictionary<Guid, string>(Names),
        new Dictionary<Guid, PeerBuildInfo>(Builds), Started, ScenarioIndex, SelectedAi, SelectedWaymark);

    public PartyRole? RoleOf(Guid peerId) =>
        ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => (PartyRole?)kv.Key).FirstOrDefault();

    public string NameOf(Guid peerId) => Names.GetValueOrDefault(peerId, "Player");
}
