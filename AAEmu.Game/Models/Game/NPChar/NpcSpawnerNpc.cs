using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using NLog;

namespace AAEmu.Game.Models.Game.NPChar;

public class NpcSpawnerNpc : Spawner<Npc>
{
    // ReSharper disable once InconsistentNaming
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// NpcSpawnerTemplateId
    /// </summary>
    public uint NpcSpawnerTemplateId { get; init; }
    /// <summary>
    /// NPC template ID or NPC group ID, selected by MemberType.
    /// </summary>
    public uint MemberId { get; set; }
    /// <summary>
    /// Authored member type: "Npc" or "NpcGroup".
    /// </summary>
    public string MemberType { get; set; }
    /// <summary>
    /// Spawn priority weight
    /// </summary>
    public float Weight { get; init; }

    public NpcSpawnerNpc()
    {
        //
    }

    /// <summary>
    /// Creates a new instance of NpcSpawnerNpcs with a Spawner template id (npc_spanwers)
    /// </summary>
    /// <param name="spawnerTemplateId"></param>
    public NpcSpawnerNpc(uint spawnerTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
    }

    public NpcSpawnerNpc(uint spawnerTemplateId, uint npcTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
        MemberId = npcTemplateId;
        MemberType = "Npc";
    }

    /// <summary>
    /// Spawns Npcs from a NpcSpawner
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns>List of newly spawned NPCs</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public List<Npc> Spawn(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        switch (MemberType)
        {
            case "Npc":
                return SpawnNpc(npcSpawner, ownerId);
            case "NpcGroup":
                return SpawnNpcGroup(npcSpawner, ownerId);
            default:
                throw new InvalidOperationException($"Tried spawning an unsupported line from NpcSpawnerNpc - Id: {Id}");
        }
    }

    /// <summary>
    /// Internal Spawn Npc function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpc(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        var npc = PrepareNpc(npcSpawner, ownerId, MemberId, npcSpawner.Position);
        if (npc == null)
            return null;
        return PublishNpc(npc) ? [npc] : [];
    }

    protected virtual Npc PrepareNpc(NpcSpawner npcSpawner, uint ownerId, uint npcTemplateId,
        WorldSpawnPosition authoredPosition)
    {
        var npc = NpcManager.Instance.Create(npcSpawner.ParentWorld, 0, npcTemplateId);
        if (npc == null)
        {
            Logger.Warn($"Npc {npcTemplateId}, from spawner Id {npcSpawner.Id} not exist at db. Spawner Position: {npcSpawner.Position}");
            return null;
        }

        npc.ParentWorld = npcSpawner.ParentWorld;
        npc.OwnerId = ownerId;
        if (npcSpawner.PendingTowerDefenseSpawnToken is { } pendingEventToken)
            npc.TowerDefenseSpawnToken = pendingEventToken;

        npc.RegisterNpcEvents();

        Logger.Trace($"Spawn npc templateId {npcTemplateId} objId {npc.ObjId} from spawnerId {NpcSpawnerTemplateId} at Position: {npcSpawner.Position}");

        GroundSurfaceResult? groundSurface = null;
        if (!npc.CanFly && !npcSpawner.PreserveAuthoredHeight &&
            npcSpawner.ParentWorld.Template.GeoData.TryGetGroundSurface(
                authoredPosition.AsPositionVector(), out var sampledSurface))
        {
            groundSurface = sampledSurface;
        }

        var runtimeSpawnPosition = CreateRuntimeSpawnPosition(authoredPosition, groundSurface,
            npcSpawner.PreserveAuthoredHeight);
        npc.Transform.ApplyWorldSpawnPosition(runtimeSpawnPosition);
        if (npc.Transform == null)
        {
            Logger.Error($"Can't spawn npc {npcTemplateId} from spawnerId {NpcSpawnerTemplateId}. Transform is null.");
            return null;
        }

        npc.Transform.InstanceId = npc.Transform.InstanceId;

        if (npc.Ai != null)
        {
            npc.Ai.HomePosition = npc.Transform.World.Position;
            npc.Ai.IdlePosition = npc.Ai.HomePosition;
            npc.Ai.GoToSpawn();
        }

        npc.Spawner = npcSpawner;
        // aaemu-cluster#92 (#96): only the main world auto-respawns NPCs on death. Instance worlds
        // (dungeons) otherwise endlessly respawned cleared packs every SpawnDelay seconds; their
        // repopulation is owned by the dungeon script instead.
        npc.Spawner.RespawnTime = npc.TowerDefenseSpawnToken == null &&
                                 npcSpawner.ParentWorld.Id == WorldManager.DefaultInstanceId
            ? (int)Random.Shared.Next(npc.Spawner.Template.SpawnDelayMin, npc.Spawner.Template.SpawnDelayMax)
            : 0;
        return npc;
    }

    protected virtual bool PublishNpc(Npc npc)
    {
        PublishNpcObject(npc);
        npc.GroupInstance?.SynchronizeCombat(npc);
        return CompleteNpcSpawn(npc);
    }

    protected virtual void PublishNpcObject(Npc npc) => npc.Spawn();

    protected virtual bool CompleteNpcSpawn(Npc npc)
    {
        var npcSpawner = npc.Spawner;
        if (npc.TowerDefenseSpawnToken?.Lifetime.IsCancelled != true)
            npc.Events.OnSpawn(npc, new OnSpawnArgs { Npc = npc });

        if (npc.TowerDefenseSpawnToken is { } eventToken &&
            !npc.ParentWorld.EventSpawnOwnership.Register(npc, eventToken))
        {
            npc.ActivePlotState?.RequestCancellation();
            npcSpawner.Despawn(npc);
            return false;
        }

        var world = WorldManager.Instance.GetWorld(npc.Transform.InstanceId);
        world.Events.OnUnitSpawn(world, new OnUnitSpawnArgs { Npc = npc });
        npc.Simulation = new Simulation(npc);

        if (npc.Ai != null && !string.IsNullOrWhiteSpace(npcSpawner.FollowPath))
        {
            if (!npc.Ai.LoadAiPathPoints(npcSpawner.FollowPath, false))
                Logger.Warn($"Failed to load {npcSpawner.FollowPath} for NPC {npc.TemplateId} ({npc.ObjId})");
        }

        return true;
    }

    /// <summary>
    /// Publishes an NPC to its world before invoking its on-spawn skills. Self-targeted skills resolve
    /// their caster through the world object registry, so raising the event before <see cref="GameObject.Spawn"/>
    /// makes those skills fail with no target. Keep this lifecycle independent from AI behavior so NPCs
    /// without an AI can also use on-spawn skills.
    /// </summary>
    internal static void SpawnAndRaiseOnSpawn(Npc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);

        npc.Spawn();
        if (npc.TowerDefenseSpawnToken?.Lifetime.IsCancelled != true)
            npc.Events.OnSpawn(npc, new OnSpawnArgs { Npc = npc });
    }

    internal static WorldSpawnPosition CreateRuntimeSpawnPosition(WorldSpawnPosition authoredPosition,
        GroundSurfaceResult? groundSurface, bool preserveAuthoredHeight = false)
    {
        var runtimePosition = authoredPosition.Clone();
        // aaemu-cluster#92 (V11): only snap spawn Z to terrain-derived surfaces. Indoor/instance ground
        // resolves to the nearest voxelized BAI navigation node, which can sit up to ~1m above the real
        // collision floor (e.g. the Sharpwind dig-site rubble). Snapping onto that lifted the researcher
        // NPCs off the floor and made clients visibly bounce them; the capture-dump Z is where the retail
        // server actually stood the NPC, so keep it unless authoritative interpolated terrain says otherwise.
        if (!preserveAuthoredHeight &&
            groundSurface is { Source: GroundSurfaceSource.Terrain } surface &&
            Math.Abs(authoredPosition.Z - surface.Height) < 1f)
            runtimePosition.Z = surface.Height;

        return runtimePosition;
    }

    /// <summary>
    /// Internal Spawn NpcGroup function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpcGroup(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        var template = NpcGroupGameData.Instance.GetNpcGroup(checked((int)MemberId));
        var members = NpcGroupGameData.Instance.GetNpcGroupMembers(checked((int)MemberId));
        if (template == null || members.Count == 0)
        {
            Logger.Warn("No members for NPC group {0} in spawner {1}", MemberId, npcSpawner.Id);
            return [];
        }

        var group = new NpcGroupInstance(template, npcSpawner, npcSpawner.Position);
        var prepared = new List<Npc>(members.Count);
        try
        {
            foreach (var member in members.OrderByDescending(member => member.IsLeader).ThenBy(member => member.Id))
            {
                var position = CreateGroupMemberPosition(group.Anchor, member);
                var npc = PrepareNpc(npcSpawner, ownerId, checked((uint)member.NpcId), position);
                if (npc == null)
                    throw new InvalidDataException($"NPC group {MemberId} has unavailable NPC {member.NpcId}.");
                prepared.Add(npc);
                group.Attach(member, npc);
            }

            npcSpawner.RegisterGroup(group, this, ownerId);
            // Publish every member before any on-spawn effect can resolve or assist a sibling.
            foreach (var npc in prepared)
                PublishNpcObject(npc);
            foreach (var npc in prepared)
                group.SynchronizeCombat(npc);
            foreach (var npc in prepared)
                if (!CompleteNpcSpawn(npc))
                    throw new InvalidOperationException($"NPC group {MemberId} could not publish every member.");
            return prepared;
        }
        catch
        {
            foreach (var npc in prepared)
                DiscardNpc(npc);
            npcSpawner.UnregisterGroup(group);
            throw;
        }
    }

    protected virtual void DiscardNpc(Npc npc) => npc.Spawner.Despawn(npc);

    internal Npc RespawnGroupMember(NpcGroupInstance group, NpcGroupMember member, uint ownerId)
    {
        var npc = PrepareNpc(group.Spawner, ownerId, checked((uint)member.NpcId), CreateGroupMemberPosition(group.Anchor, member));
        if (npc == null)
            return null;
        try
        {
            group.Attach(member, npc);
            if (PublishNpc(npc))
                return npc;
        }
        catch
        {
            DiscardNpc(npc);
            group.Detach(npc);
            throw;
        }
        DiscardNpc(npc);
        group.Detach(npc);
        return null;
    }

    internal static WorldSpawnPosition CreateGroupMemberPosition(WorldSpawnPosition anchor, NpcGroupMember member)
    {
        var position = anchor.Clone();
        // Server interpretation: authored offsets use the spawner's local yaw basis.
        var cosine = MathF.Cos(anchor.Yaw);
        var sine = MathF.Sin(anchor.Yaw);
        position.X += member.FormationOffsetX * cosine - member.FormationOffsetY * sine;
        position.Y += member.FormationOffsetX * sine + member.FormationOffsetY * cosine;
        position.Z += member.FormationOffsetZ;
        return position;
    }
}
