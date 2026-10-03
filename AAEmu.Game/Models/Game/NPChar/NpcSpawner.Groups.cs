using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.NpcGroup;

namespace AAEmu.Game.Models.Game.NPChar;

public partial class NpcSpawner
{
    private sealed record PendingGroupMember(NpcGroupMember Definition, DateTime ReadyAt);

    private sealed class GroupSpawnState(NpcSpawnerNpc definition, uint ownerId)
    {
        public NpcSpawnerNpc Definition { get; } = definition;
        public uint OwnerId { get; } = ownerId;
        public Dictionary<int, PendingGroupMember> Pending { get; } = [];
        public DateTime ReplacementAt { get; set; }
        public bool SuppressRespawn { get; set; }
        public int RespawnDelay { get; set; }
    }

    private Dictionary<NpcGroupInstance, GroupSpawnState> _groups = [];

    internal void RegisterGroup(NpcGroupInstance group, NpcSpawnerNpc definition, uint ownerId)
    {
        lock (_spawnLock)
            _groups.Add(group, new GroupSpawnState(definition, ownerId)
            {
                RespawnDelay = RespawnTime,
                SuppressRespawn = ParentWorld?.Id != WorldManager.DefaultInstanceId ||
                    group.GetMembers().Any(npc => npc.TowerDefenseSpawnToken != null) || RespawnTime <= 0
            });
    }

    internal void UnregisterGroup(NpcGroupInstance group)
    {
        lock (_spawnLock)
        {
            _groups.Remove(group);
            group.Retire();
        }
    }

    private void RetireEmptySuppressedGroups()
    {
        foreach (var (group, state) in _groups.ToArray())
            if (state.SuppressRespawn && group.GetMembers().Length == 0)
                UnregisterGroup(group);
    }

    internal void SuppressAutomaticRespawn(Npc npc)
    {
        lock (_spawnLock)
        {
            if (!ReferenceEquals(npc?.Spawner, this))
                return;
            RespawnTime = 0;
            if (npc.GroupInstance is { } group && _groups.TryGetValue(group, out var state))
            {
                state.SuppressRespawn = true;
                state.Pending.Clear();
                state.ReplacementAt = DateTime.MinValue;
            }
        }
    }

    private int CountPopulation()
    {
        lock (_spawnLock)
        {
            var npcs = SpawnedNpcs.TryGetValue(SpawnerId, out var list) ? list.ToArray() : [];
            var groups = npcs.Where(npc => npc.GroupInstance != null).Select(npc => npc.GroupInstance).ToHashSet();
            groups.UnionWith(_groups.Keys.Where(group => !group.IsRetired));
            return npcs.Count(npc => npc.GroupInstance == null) + groups.Count;
        }
    }

    private void QueueGroupRespawn(Npc npc)
    {
        var group = npc.GroupInstance;
        if (group == null || npc.GroupMember == null || !_groups.TryGetValue(group, out var state) ||
            state.SuppressRespawn || !IsSpawningScheduleEnabled())
            return;
        var deathTime = npc.DeadTime == DateTime.MinValue ? DateTime.UtcNow : npc.DeadTime;
        state.Pending.TryAdd(npc.GroupMember.Id, new(npc.GroupMember, deathTime.AddSeconds(state.RespawnDelay)));
        if (group.GetMembers().All(member => member.Hp <= 0 || member.Despawned || ReferenceEquals(member, npc)))
            state.ReplacementAt = state.Pending.Values.Max(member => member.ReadyAt);
    }

    internal void ProcessGroupRespawns(DateTime utcNow)
    {
        lock (_spawnLock)
        {
            if (!IsActive || !IsSpawningScheduleEnabled())
                return;
            foreach (var (group, state) in _groups.ToArray())
            {
                if (group.IsRetired)
                {
                    _groups.Remove(group);
                    continue;
                }
                var members = group.GetMembers();
                if (state.SuppressRespawn)
                    continue;
                if (!members.Any(member => member.Hp > 0 && !member.Despawned))
                {
                    // A whole wipe uses one outer delay. Corpse removal must finish first.
                    if (members.Length == 0 && state.ReplacementAt != DateTime.MinValue && utcNow >= state.ReplacementAt)
                        UnregisterGroup(group);
                    else if (members.Length == 0 && state.Pending.Count == 0)
                        UnregisterGroup(group);
                    continue;
                }
                if (!group.Template.EnableRespawn)
                    continue;
                foreach (var (id, pending) in state.Pending.ToArray())
                {
                    if (utcNow < pending.ReadyAt || group.GetMembers().Any(member => member.GroupMember?.Id == id))
                        continue;
                    var replacement = state.Definition.RespawnGroupMember(group, pending.Definition, state.OwnerId);
                    if (replacement == null)
                        continue;
                    AddNpcToSpawned(SpawnerId, replacement);
                    state.Pending.Remove(id);
                    state.ReplacementAt = DateTime.MinValue;
                }
            }
        }
    }

    private void CancelGroupRespawns(bool retire)
    {
        foreach (var (group, state) in _groups.ToArray())
        {
            state.Pending.Clear();
            state.ReplacementAt = DateTime.MinValue;
            if (retire)
                UnregisterGroup(group);
        }
    }
}
