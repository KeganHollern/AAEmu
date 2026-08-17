using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// Per-world spatial sensor for DoodadFuncAreaTrigger (doodad_funcs rows with func_type
/// DoodadFuncAreaTrigger, e.g. the Sharpwind Mines collapsing bridge 5058 and stalactites 5364/5365).
/// Armed state must live per WorldInstance — func templates are shared objects, so keeping any
/// "fired" latch on them bleeds across dungeon instances. Owned lazily by
/// <see cref="WorldInstance.DoodadAreaTriggers"/>; a 500ms tick (same pattern as
/// SphereQuestManager.Initialize) is subscribed only once the first sensor arms. aaemu-cluster#92 / #95.
/// </summary>
public class DoodadAreaTriggerRegistry(WorldInstance owner) : IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Sensed radius (m). The compact doodad_func_area_triggers table has no radius column (the
    /// client-side trigger volume is part of the doodad model), so a constant is used that covers
    /// the walkable width of the known users (bridge deck, stalactite drop zones). aaemu-cluster#95.
    /// </summary>
    public const float DefaultTriggerRadiusMeters = 12f;

    private sealed class ArmedTrigger
    {
        public Doodad Doodad;
        public DoodadFuncAreaTrigger Template;
        public int NextPhase;
        /// <summary>Phase the sensor was armed for; a silent phase drift invalidates the entry.</summary>
        public uint ArmedFuncGroupId;
    }

    private readonly object _lock = new();
    private readonly Dictionary<uint, ArmedTrigger> _armedByObjId = new();
    private bool _tickSubscribed;

    /// <summary>
    /// Re-evaluates arming for the doodad's current phase; called at the end of every
    /// Doodad.DoChangePhase. Any previous sensor for this doodad is disarmed first, so a trigger
    /// fires at most once per phase entry (re-arming only happens if the NEW phase also carries a
    /// DoodadFuncAreaTrigger func). aaemu-cluster#95.
    /// </summary>
    public void OnDoodadPhaseChanged(Doodad doodad)
    {
        if (doodad == null)
            return;

        var funcs = doodad.CurrentFuncs; // snapshot; the setter swaps the list reference atomically
        lock (_lock)
        {
            _armedByObjId.Remove(doodad.ObjId);
            if (funcs == null)
                return;

            foreach (var func in funcs)
            {
                if (func.FuncType != "DoodadFuncAreaTrigger")
                    continue;

                if (DoodadManager.Instance.GetFuncTemplate(func.FuncId, func.FuncType) is not DoodadFuncAreaTrigger template)
                {
                    Logger.Warn($"DoodadFuncAreaTrigger template missing for funcId {func.FuncId} (doodad {doodad.TemplateId})");
                    continue;
                }

                if (!template.IsEnter)
                {
                    // is_enter=f (leave-volume) semantics have no known content users yet; do not arm.
                    Logger.Warn($"DoodadFuncAreaTrigger is_enter=false unsupported (doodad {doodad.TemplateId}, funcId {func.FuncId}); aaemu-cluster#95");
                    continue;
                }

                if (func.NextPhase <= 0)
                    continue;

                _armedByObjId[doodad.ObjId] = new ArmedTrigger
                {
                    Doodad = doodad,
                    Template = template,
                    NextPhase = func.NextPhase,
                    ArmedFuncGroupId = doodad.FuncGroupId
                };
                EnsureTickSubscribedUnderLock();
                break; // the data never carries more than one sensor per phase
            }
        }
    }

    /// <summary>Disarms any sensor watching a doodad that is being removed from the world. aaemu-cluster#95.</summary>
    public void OnDoodadDeleted(Doodad doodad)
    {
        if (doodad == null)
            return;

        lock (_lock)
        {
            _armedByObjId.Remove(doodad.ObjId);
        }
    }

    private void Tick(TimeSpan delta)
    {
        try
        {
            List<(ArmedTrigger armed, BaseUnit unit)> fired = null;
            lock (_lock)
            {
                if (_armedByObjId.Count == 0)
                    return;

                List<uint> stale = null;
                foreach (var armed in _armedByObjId.Values)
                {
                    // Defensive: phase moved on without OnDoodadPhaseChanged (should not happen).
                    if (armed.Doodad.FuncGroupId != armed.ArmedFuncGroupId)
                    {
                        (stale ??= []).Add(armed.Doodad.ObjId);
                        continue;
                    }

                    var unit = FindTriggeringUnit(armed);
                    if (unit == null)
                        continue;

                    (fired ??= []).Add((armed, unit));
                }

                if (stale != null)
                    foreach (var objId in stale)
                        _armedByObjId.Remove(objId);

                // Disarm before firing so each sensor fires exactly once per phase entry.
                if (fired != null)
                    foreach (var (armed, _) in fired)
                        _armedByObjId.Remove(armed.Doodad.ObjId);
            }

            if (fired == null)
                return;

            // Fire outside the lock: DoChangePhase re-enters OnDoodadPhaseChanged to arm the new phase.
            foreach (var (armed, unit) in fired)
            {
                Logger.Debug($"DoodadFuncAreaTrigger fired: doodad {armed.Doodad.TemplateId} (obj {armed.Doodad.ObjId}) -> phase {armed.NextPhase}, triggered by obj {unit.ObjId} in {owner}");
                armed.Doodad.DoChangePhase(unit, armed.NextPhase);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e);
        }
    }

    private BaseUnit FindTriggeringUnit(ArmedTrigger armed)
    {
        var pos = armed.Doodad.Transform.World.Position;
        const float radiusSq = DefaultTriggerRadiusMeters * DefaultTriggerRadiusMeters;

        if (armed.Template.NpcId > 0)
        {
            // npc_id variant (stalactites 5364/5365): sense a specific NPC template instead of players.
            var npc = owner.GetNpcByTemplateId(armed.Template.NpcId);
            if (npc == null || npc.IsDead)
                return null;
            return Vector3.DistanceSquared(npc.Transform.World.Position, pos) <= radiusSq ? npc : null;
        }

        foreach (var character in owner.GetAllCharacters())
        {
            if (character.IsDead)
                continue;
            if (Vector3.DistanceSquared(character.Transform.World.Position, pos) <= radiusSq)
                return character;
        }

        return null;
    }

    /// <summary>Tick starts with the first armed sensor; caller must hold <see cref="_lock"/>.</summary>
    private void EnsureTickSubscribedUnderLock()
    {
        if (_tickSubscribed)
            return;
        _tickSubscribed = true;
        TickManager.Instance.OnTick.Subscribe(Tick, TimeSpan.FromMilliseconds(500), true);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _armedByObjId.Clear();
            if (!_tickSubscribed)
                return;
            _tickSubscribed = false;
            TickManager.Instance.OnTick.UnSubscribe(Tick);
        }
    }
}
