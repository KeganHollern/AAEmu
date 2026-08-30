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
        npc.TowerDefenseSpawnToken = token;
        return _owned.TryAdd(npc.ObjId, new OwnedEventNpc(npc, token));
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
            owned.Npc.TowerDefenseSpawnToken = null;
    }

    public void Clear()
    {
        foreach (var objId in _owned.Keys)
            Unregister(objId);
    }
}

public sealed record OwnedEventNpc(Npc Npc, TowerDefenseSpawnToken Token);
