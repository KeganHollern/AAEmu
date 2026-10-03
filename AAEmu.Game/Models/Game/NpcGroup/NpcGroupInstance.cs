using System.Numerics;

using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.NpcGroup;

/// <summary>One spawned occurrence of an authored NPC group.</summary>
public sealed partial class NpcGroupInstance(NpcGroup template, NpcSpawner spawner, WorldSpawnPosition anchor)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Npc> _members = [];

    public NpcGroup Template { get; } = template;
    public NpcSpawner Spawner { get; } = spawner;
    public WorldSpawnPosition Anchor { get; } = anchor.Clone();
    public bool IsRetired { get; private set; }

    public Npc Leader => GetMembers().FirstOrDefault(npc => npc.GroupMember?.IsLeader == true);
    public Npc MoveLeader => GetMembers().FirstOrDefault(npc => npc.GroupMember?.IsMoveLeader == true);

    public Npc[] GetMembers()
    {
        lock (_sync)
            return _members.OrderBy(member => member.Key).Select(member => member.Value).ToArray();
    }

    public bool TryGetFormationPoint(Npc npc, out Vector3 position, out float tension)
    {
        position = npc.Transform.World.Position;
        tension = npc.GroupMember?.FormationTension ?? 0;
        if (IsRetired || !ReferenceEquals(npc.GroupInstance, this) || npc.GroupMember == null || npc.GroupMember.IsMoveLeader)
            return false;
        var leader = MoveLeader;
        var leaderMember = leader?.GroupMember;
        var followerMember = npc.GroupMember;
        if (leaderMember == null || followerMember == null || leader.Hp <= 0 || leader.Despawned)
            return true;
        var offset = new Vector3(followerMember.FormationOffsetX - leaderMember.FormationOffsetX,
            followerMember.FormationOffsetY - leaderMember.FormationOffsetY,
            followerMember.FormationOffsetZ - leaderMember.FormationOffsetZ);
        position = leader.Transform.World.Position + Vector3.Transform(offset,
            Matrix4x4.CreateRotationZ(leader.Transform.World.Rotation.Z));
        return true;
    }

    internal void Attach(NpcGroupMember member, Npc npc)
    {
        lock (_sync)
        {
            if (IsRetired || member.NpcGroupId != Template.Id || npc.GroupInstance != null ||
                !_members.TryAdd(member.Id, npc))
                throw new InvalidOperationException("The NPC cannot join this group occurrence.");
            npc.GroupInstance = this;
            npc.GroupMember = member;
        }
    }

    public void Detach(Npc npc)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(npc.GroupInstance, this) || npc.GroupMember == null ||
                !_members.TryGetValue(npc.GroupMember.Id, out var member) || !ReferenceEquals(member, npc))
                return;
            _members.Remove(npc.GroupMember.Id);
            npc.GroupInstance = null;
            npc.GroupMember = null;
        }
        PruneSharedThreat();
    }

    internal void Retire()
    {
        Npc[] members;
        lock (_sync)
        {
            IsRetired = true;
            members = _members.Values.ToArray();
            foreach (var npc in _members.Values)
            {
                npc.GroupInstance = null;
                npc.GroupMember = null;
            }
            _members.Clear();
        }
        ClearSharedThreat();
        // Schedule expiry can retire a group before queued corpse removal completes.
        // Drop local subscriptions outside both group locks during that interval.
        foreach (var npc in members)
            npc.ClearAllAggro();
    }
}
