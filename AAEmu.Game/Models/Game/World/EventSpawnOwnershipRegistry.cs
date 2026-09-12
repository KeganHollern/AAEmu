using System.Collections.Concurrent;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.Game.Models.Game.World;

public sealed class EventSpawnOwnershipRegistry
{
    private readonly ConcurrentDictionary<uint, OwnedEventNpc> _owned = new();

    public bool Register(Npc npc, TowerDefenseSpawnToken token)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(token);
        return token.Lifetime.TryRegister(() =>
        {
            if (!_owned.TryAdd(npc.ObjId, new OwnedEventNpc(npc, token)))
                return false;
            npc.TowerDefenseSpawnToken = token;
            return true;
        });
    }

    public bool TryGet(uint objId, out OwnedEventNpc owned) => _owned.TryGetValue(objId, out owned);

    public IReadOnlyList<OwnedEventNpc> GetOccurrence(string occurrenceKey) =>
        _owned.Values.Where(value => value.Token.OccurrenceKey == occurrenceKey).ToList();

    public IReadOnlyList<OwnedEventNpc> GetChildren(uint creatorObjId) =>
        _owned.Values.Where(value =>
            value.Token.CreatorObjId == creatorObjId && value.Token.DespawnOnCreatorDeath).ToList();

    public void Unregister(uint objId)
    {
        if (_owned.TryRemove(objId, out var owned))
        {
            owned.Npc.ActivePlotState?.RequestCancellation();
            owned.Npc.TowerDefenseSpawnToken = null;
        }
    }

    public void Clear()
    {
        foreach (var owned in _owned.Values)
            owned.Token.Lifetime.Cancel();
        foreach (var objId in _owned.Keys)
            Unregister(objId);
    }

    public void CancelOccurrence(string occurrenceKey)
    {
        // Cancellation and registration serialize on the shared lifetime. Once this returns,
        // a cleanup snapshot includes every accepted descendant; later arrivals are rejected.
        foreach (var owned in GetOccurrence(occurrenceKey))
            owned.Token.Lifetime.Cancel();
    }
}

public sealed record OwnedEventNpc(Npc Npc, TowerDefenseSpawnToken Token);
