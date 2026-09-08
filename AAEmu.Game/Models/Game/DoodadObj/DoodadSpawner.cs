using System.ComponentModel;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.Game.Models.Tasks.World;

using Newtonsoft.Json;

using NLog;

namespace AAEmu.Game.Models.Game.DoodadObj;

public class DoodadSpawner : Spawner<Doodad>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    [JsonIgnore]
    public WorldInstance ParentWorld { get; set; }
    /// <summary>
    /// Default scale for spawned Doodads
    /// </summary>
    public float Scale { get; set; }

    /// <summary>
    /// Reference to last spawned Doodad
    /// </summary>
    public Doodad Last { get; set; }

    /// <summary>
    /// List of Doodads spawned by this spawner
    /// </summary>
    internal List<Doodad> _spawned;
    // Phase callbacks can persist items and despawn from inside a player interaction.
    // Use the same reentrant gate so a timer never owns a spawner lock while waiting
    // for an interaction that is itself waiting to despawn from that spawner.
    private readonly object _spawnLock = SaveManager.PersistenceSyncRoot;
    private DoodadSpawnerDoSpawnTask _spawnTask;
    private DoodadSpawnerDoDespawnTask _despawnTask;
    private uint _previousObjectId;

    /// <summary>
    /// Number of allowed spawns
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    [DefaultValue(1f)]
    public uint Count { get; set; } = 1;

    /// <summary>
    /// Related Ids for FuncPulse
    /// </summary>
    public List<uint> RelatedIds { get; set; }

    /// <summary>
    /// Overrides Doodad template for respawns
    /// </summary>
    public uint RespawnDoodadTemplateId { get; set; }

    public DoodadSpawner()
    {
        _spawned = [];
        Count = 1;
        Scale = 1f;
    }

    /*
    public DoodadSpawner(uint id, uint unitId, WorldSpawnPosition position)
    {
        Id = id;
        UnitId = unitId;
        Position = position;
    }
    */

    /// <summary>
    /// Spawn a doodad in the world with a character as owner. Mostly used for player created spawns
    /// </summary>
    /// <param name="objId">instance id of the doodad</param>
    /// <param name="itemId">template id of the doodad</param>
    /// <param name="charId">instance id of the character</param>
    /// <returns>Created doodad reference</returns>
    public override Doodad Spawn(uint objId, ulong itemId, uint charId)
    {
        lock (_spawnLock)
        {
            return SpawnOwned(objId, itemId, charId);
        }
    }

    private Doodad SpawnOwned(uint objId, ulong itemId, uint charId)
    {
        if (!GameScheduleManager.IsActivePeriod(GameScheduleManager.Instance.GetPeriodStatusDoodad((int)UnitId)))
            return null;

        var character = WorldManager.Instance.GetCharacterByObjId(charId);
        ParentWorld = character.ParentWorld;
        var doodad = CreateOccurrence(objId, UnitId, character);

        if (doodad == null)
        {
            Logger.Warn($"Doodad {UnitId}, from spawn not exist at db");
            return null;
        }

        doodad.Spawner = this;
        doodad.Transform.ApplyWorldSpawnPosition(Position);
        doodad.QuestGlow = 0u; // TODO: make this OOP
        doodad.ItemId = itemId;

        // TODO for test
        doodad.PlantTime = DateTime.UtcNow;

        if (Scale > 0)
        {
            doodad.SetScale(Scale);
        }

        if (doodad.Transform == null)
        {
            Logger.Error($"Can't spawn doodad {UnitId} from spawn {Id}");
            return null;
        }

        Last = doodad;
        DoSpawn();// schedule check and spawn
        return doodad;
    }

    /// <summary>
    /// Spawn a doodad (mostly used by respawns)
    /// </summary>
    /// <param name="objId"></param>
    /// <returns></returns>
    public override Doodad Spawn(uint objId)
    {
        lock (_spawnLock)
        {
            if (objId != 0 || ParentWorld == null)
                return null;
            if (Last != null)
            {
                DoSpawn();
                return Last;
            }
            var schedules = GameScheduleManager.Instance;
            if (!GameScheduleManager.IsActivePeriod(schedules.GetPeriodStatusDoodad((int)UnitId)))
            {
                ScheduleSpawn(schedules.GetDoodadRemainingTime((int)UnitId));
                return null;
            }
            return SpawnOccurrence();
        }
    }

    private Doodad SpawnOccurrence()
    {
        var newUnitId = RespawnDoodadTemplateId > 0 ? RespawnDoodadTemplateId : UnitId;
        RespawnDoodadTemplateId = 0; // reset it after 1 spawn

        var doodad = CreateOccurrence(0, newUnitId);
        if (doodad == null)
        {
            Logger.Warn($"Doodad Template {newUnitId}, used in Spawn() does not exist in db");
            return null;
        }

        doodad.Spawner = this;
        doodad.Transform.ApplyWorldSpawnPosition(Position);
        // TODO for test
        doodad.PlantTime = DateTime.UtcNow;
        if (Scale > 0)
        {
            doodad.SetScale(Scale);
        }

        if (doodad.Transform == null)
        {
            Logger.Error($"Can't spawn doodad {newUnitId} from spawn {Id}");
            return null;
        }

        Last = doodad;
        DoSpawn();// schedule check and spawn
        return doodad;
    }

    private Doodad CreateOccurrence(uint objectId, uint templateId, GameObject owner = null)
    {
        var allocated = objectId == 0;
        if (allocated)
        {
            objectId = ObjectIdManager.Instance.GetNextId();
            if (objectId == _previousObjectId)
            {
                var replacement = ObjectIdManager.Instance.GetNextId();
                ObjectIdManager.Instance.ReleaseId(objectId);
                objectId = replacement;
            }
        }

        try
        {
            var doodad = CreateDoodad(objectId, templateId, owner);
            if (doodad == null && allocated)
                ObjectIdManager.Instance.ReleaseId(objectId);
            return doodad;
        }
        catch
        {
            if (allocated)
                ObjectIdManager.Instance.ReleaseId(objectId);
            throw;
        }
    }

    protected virtual Doodad CreateDoodad(uint objectId, uint templateId, GameObject owner)
    {
        return DoodadManager.Instance.Create(ParentWorld, objectId, templateId, owner);
    }

    /// <summary>
    /// Despawn target Doodad
    /// </summary>
    /// <param name="doodad"></param>
    public override void Despawn(Doodad doodad)
    {
        lock (_spawnLock)
        {
            if (doodad == null || !ReferenceEquals(Last, doodad) && !_spawned.Contains(doodad))
                return;

            CancelDespawnTask();
            doodad.Delete();
            _spawned.Remove(doodad);
            // Respawns create a fresh object, so the retired occurrence never retains its ID.
            ObjectIdManager.Instance.ReleaseId(doodad.ObjId);
            if (ReferenceEquals(Last, doodad))
                Last = null;
        }
    }

    /// <summary>
    /// Despawns target Doodad and schedules a respawn
    /// </summary>
    /// <param name="doodad"></param>
    public void DecreaseCount(Doodad doodad)
    {
        lock (_spawnLock)
        {
            if (doodad == null || !ReferenceEquals(Last, doodad) || !_spawned.Contains(doodad))
                return;

            if (RespawnTime > 0)
            {
                doodad.Respawn = DateTime.UtcNow.AddSeconds(RespawnTime);
                doodad.ParentWorld.SpawnManager.AddRespawn(doodad);
            }
            Despawn(doodad);
        }
    }

    /// <summary>
    /// Does a despawn for target Doodad 
    /// </summary>
    /// <param name="doodad"></param>
    public void DoDespawn(Doodad doodad)
    {
        lock (_spawnLock)
        {
            // A delayed callback from an older occurrence must not retire its replacement.
            if (doodad == null || !ReferenceEquals(Last, doodad) || !_spawned.Contains(doodad))
                return;

            CancelDespawnTask();
            var schedules = GameScheduleManager.Instance;
            var status = schedules.GetPeriodStatusDoodad((int)UnitId);
            if (status != GameScheduleManager.PeriodStatus.NotFound && GameScheduleManager.IsActivePeriod(status))
            {
                ScheduleDespawn(schedules.GetDoodadRemainingTime((int)UnitId, false));
                return;
            }

            if (status != GameScheduleManager.PeriodStatus.NotFound)
            {
                doodad.FuncTask?.Retire();
                doodad.FuncTask = null;
            }
            Despawn(doodad);
            if (status != GameScheduleManager.PeriodStatus.NotFound)
                ScheduleSpawn(schedules.GetDoodadRemainingTime((int)UnitId));
        }
    }

    /// <summary>
    /// Spawns one occurrence only while any associated schedule is active.
    /// </summary>
    public void DoSpawn()
    {
        lock (_spawnLock)
        {
            CancelSpawnTask();
            if (ParentWorld == null)
                return;

            var schedules = GameScheduleManager.Instance;
            var status = schedules.GetPeriodStatusDoodad((int)UnitId);
            if (!GameScheduleManager.IsActivePeriod(status))
            {
                ScheduleSpawn(schedules.GetDoodadRemainingTime((int)UnitId));
                return;
            }

            if (Last == null)
            {
                // A scheduled occurrence always starts with a new object and initial phase.
                SpawnOccurrence();
                return;
            }
            if (_spawned.Contains(Last))
                return;

            var doodad = Last;
            _previousObjectId = doodad.ObjId;
            doodad.Spawn();

            if (doodad.Transform.WorldId != WorldManager.DefaultWorldTemplateId &&
                doodad.ParentWorld?.DungeonInstance is not null)
            {
                doodad.ParentWorld.Events.OnDoodadSpawn(doodad.ParentWorld, new OnDoodadSpawnArgs { Doodad = doodad });
            }

            _spawned.Add(doodad);
            if (status != GameScheduleManager.PeriodStatus.NotFound)
                ScheduleDespawn(schedules.GetDoodadRemainingTime((int)UnitId, false));
        }
    }

    private void ScheduleSpawn(TimeSpan delay)
    {
        if (delay == TimeSpan.MaxValue)
            return;
        CancelSpawnTask();
        _spawnTask = new DoodadSpawnerDoSpawnTask(this);
        TaskManager.Instance.Schedule(_spawnTask, delay);
    }

    internal void ExecutePhaseTask(Doodad owner, DoodadFuncTask task, Action action)
    {
        lock (_spawnLock)
        {
            if (ReferenceEquals(owner.FuncTask, task))
                action();
        }
    }

    private void ScheduleDespawn(TimeSpan delay)
    {
        if (delay == TimeSpan.MaxValue)
            return;
        CancelDespawnTask();
        _despawnTask = new DoodadSpawnerDoDespawnTask(Last);
        TaskManager.Instance.Schedule(_despawnTask, delay);
    }

    private void CancelSpawnTask()
    {
        if (_spawnTask == null)
            return;
        TaskManager.Instance.Cancel(_spawnTask);
        _spawnTask = null;
    }

    private void CancelDespawnTask()
    {
        if (_despawnTask == null)
            return;
        TaskManager.Instance.Cancel(_despawnTask);
        _despawnTask = null;
    }
}
