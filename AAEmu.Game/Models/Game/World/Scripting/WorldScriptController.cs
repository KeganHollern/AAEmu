using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World.Transform;

using NLog;

namespace AAEmu.Game.Models.Game.World.Scripting;

/// <summary>
/// Per-world-instance runtime for <see cref="WorldScriptRule"/> scripts
/// (aaemu-cluster#92). Subscribes to doodad phase changes and, when any rule
/// has an area condition, polls player positions on a 500ms tick. Every rule
/// fires at most once per instance; all evaluation is serialized on one lock
/// because phase events arrive from game threads while area checks run on the
/// tick thread.
/// </summary>
public sealed class WorldScriptController
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private const int AreaTickMs = 500;

    private readonly WorldInstance _world;
    private readonly List<WorldScriptRule> _rules;
    private readonly HashSet<WorldScriptRule> _fired = [];
    private readonly object _sync = new();
    private bool _tickSubscribed;
    private bool _disposed;

    private WorldScriptController(WorldInstance world, List<WorldScriptRule> rules)
    {
        _world = world;
        _rules = rules;
    }

    /// <summary>
    /// Creates and starts a controller for the world when its template ships a
    /// dungeon_scripts.json; returns null otherwise (zero overhead for the
    /// overwhelming majority of worlds).
    /// </summary>
    public static WorldScriptController TryCreate(WorldInstance world)
    {
        var rules = WorldScriptTemplate.GetForWorld(world.Template?.Name ?? string.Empty);
        if (rules == null || rules.Count == 0)
            return null;

        var controller = new WorldScriptController(world, rules);
        controller.Start();
        return controller;
    }

    private void Start()
    {
        _world.DoodadPhaseChanged += OnDoodadPhaseChanged;
        _world.NpcKilled += OnNpcKilled;
        if (_rules.Exists(r => r.OnPlayerEnterArea != null))
        {
            TickManager.Instance.OnTick.Subscribe(AreaTick, TimeSpan.FromMilliseconds(AreaTickMs), true);
            _tickSubscribed = true;
        }

        Logger.Info($"World scripts armed for {_world.Template?.Name} ({_world.Id}): {_rules.Count} rule(s)");
    }

    /// <summary>Detaches all hooks; called from WorldInstance.CleanupInstance.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _world.DoodadPhaseChanged -= OnDoodadPhaseChanged;
        _world.NpcKilled -= OnNpcKilled;
        if (_tickSubscribed)
            TickManager.Instance.OnTick.UnSubscribe(AreaTick);
    }

    private void OnDoodadPhaseChanged(Doodad doodad, uint funcGroupId)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            foreach (var rule in _rules)
            {
                if (_fired.Contains(rule))
                    continue;

                if (rule.OnDoodadPhase != null &&
                    rule.OnDoodadPhase.DoodadTemplateId == doodad.TemplateId &&
                    rule.OnDoodadPhase.FuncGroupId == funcGroupId &&
                    IsNear(rule.OnDoodadPhase.Near, doodad))
                {
                    Fire(rule);
                    continue;
                }

                if (rule.OnAllDoodadsPhase != null &&
                    rule.OnAllDoodadsPhase.DoodadTemplateId == doodad.TemplateId &&
                    AllDoodadsInPhase(rule.OnAllDoodadsPhase))
                {
                    Fire(rule);
                }
            }
        }
    }

    private void OnNpcKilled(NPChar.Npc npc)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            foreach (var rule in _rules)
            {
                if (_fired.Contains(rule))
                    continue;

                if (rule.OnNpcKilled != null && rule.OnNpcKilled.NpcTemplateIds.Contains(npc.TemplateId))
                    Fire(rule);
            }
        }
    }

    /// <summary>Null or zero-radius filters match everything.</summary>
    private static bool IsNear(WorldScriptArea near, Doodad doodad)
    {
        if (near == null || near.Radius <= 0)
            return true;
        var pos = doodad.Transform?.World?.Position ?? Vector3.Zero;
        return Vector3.DistanceSquared(pos, new Vector3(near.X, near.Y, near.Z)) <= near.Radius * near.Radius;
    }

    /// <summary>
    /// True when at least one doodad of the template is live and every live one
    /// is in an accepted func group. Doodads that already despawned (e.g. via
    /// DoodadFuncFinal) no longer gate completion — by the time they despawn
    /// they necessarily passed through the accepted phase.
    /// </summary>
    private bool AllDoodadsInPhase(WorldScriptAllDoodadsPhase condition)
    {
        var seen = false;
        foreach (var doodad in _world.GetAllDoodads())
        {
            if (doodad.TemplateId != condition.DoodadTemplateId)
                continue;
            seen = true;
            if (!condition.FuncGroupIds.Contains(doodad.FuncGroupId))
                return false;
        }

        return seen;
    }

    private void AreaTick(TimeSpan delta)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            List<Character> characters = null;
            foreach (var rule in _rules)
            {
                if (rule.OnPlayerEnterArea == null || _fired.Contains(rule))
                    continue;

                characters ??= _world.GetAllCharacters();
                var area = rule.OnPlayerEnterArea;
                var center = new Vector3(area.X, area.Y, area.Z);
                foreach (var character in characters)
                {
                    var pos = character?.Transform?.World?.Position ?? Vector3.Zero;
                    if (Vector3.DistanceSquared(pos, center) <= area.Radius * area.Radius)
                    {
                        Fire(rule);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Runs a rule's actions; the rule never fires again in this instance.</summary>
    private void Fire(WorldScriptRule rule)
    {
        _fired.Add(rule);
        Logger.Info($"World script rule fired in {_world.Template?.Name} ({_world.Id}): {rule.Name}");

        foreach (var action in rule.Actions)
        {
            try
            {
                Execute(action);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"World script action failed in rule '{rule.Name}'");
            }
        }
    }

    private void Execute(WorldScriptAction action)
    {
        if (action.ActivateNpcSpawners != null)
        {
            foreach (var spawnerTemplateId in action.ActivateNpcSpawners)
                foreach (var spawner in _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(spawnerTemplateId))
                    spawner.Activate();
        }

        if (action.DeactivateNpcSpawners != null)
        {
            foreach (var spawnerTemplateId in action.DeactivateNpcSpawners)
                foreach (var spawner in _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(spawnerTemplateId))
                    spawner.Deactivate();
        }

        if (action.DespawnNpcSpawners != null)
        {
            foreach (var spawnerTemplateId in action.DespawnNpcSpawners)
            {
                foreach (var spawner in _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(spawnerTemplateId))
                {
                    spawner.Deactivate();
                    spawner.DespawnAll();
                }
            }
        }

        if (action.ChangeDoodadPhase != null)
        {
            foreach (var doodad in _world.GetAllDoodads())
            {
                if (doodad.TemplateId != action.ChangeDoodadPhase.DoodadTemplateId)
                    continue;
                if (doodad.FuncGroupId == action.ChangeDoodadPhase.FuncGroupId)
                    continue;
                if (!IsNear(action.ChangeDoodadPhase.Near, doodad))
                    continue;
                doodad.DoChangePhase(null, (int)action.ChangeDoodadPhase.FuncGroupId);
            }
        }

        if (action.SpawnDoodads != null)
        {
            foreach (var spawn in action.SpawnDoodads)
            {
                var spawner = new DoodadSpawner
                {
                    ParentWorld = _world,
                    UnitId = spawn.TemplateId,
                    Position = new WorldSpawnPosition
                    {
                        WorldId = _world.Id,
                        X = spawn.X,
                        Y = spawn.Y,
                        Z = spawn.Z,
                        Yaw = spawn.Yaw
                    }
                };
                var doodad = spawner.Spawn(0);
                if (doodad == null)
                    Logger.Warn($"World script SpawnDoodads: template {spawn.TemplateId} failed to spawn in {_world.Template?.Name} ({_world.Id})");
            }
        }

        if (action.Say != null)
        {
            if (action.Say.DelaySeconds > 0)
                TaskManager.Instance.Schedule(new Tasks.World.WorldScriptSayTask(_world, action.Say),
                    TimeSpan.FromSeconds(action.Say.DelaySeconds));
            else
                SayNow(_world, action.Say);
        }
    }

    /// <summary>
    /// Shows a chat bubble above a live NPC of the template. Fails soft when the NPC is not
    /// (yet) in the world — event-spawned NPCs appear on the next world tick, which is why
    /// scripted lines usually carry a small DelaySeconds. (aaemu-cluster#92 validation)
    /// </summary>
    internal static void SayNow(WorldInstance world, WorldScriptSay say)
    {
        var npc = world.GetNpcByTemplateId(say.NpcTemplateId);
        if (npc == null || npc.IsDead)
        {
            Logger.Warn($"World script Say: npc {say.NpcTemplateId} not alive in {world.Template?.Name} ({world.Id})");
            return;
        }

        if (say.BubbleId > 0)
            // Retail bubble row: send the id with empty text; the client renders its localized line
            // exactly like the BubbleEffect skill path does. (aaemu-cluster#92)
            npc.BroadcastPacket(new Core.Packets.G2C.SCChatBubblePacket(npc.ObjId, 1, 2, say.BubbleId, string.Empty), true);
        else
            npc.BroadcastPacket(new Core.Packets.G2C.SCChatBubblePacket(npc.ObjId, 1, 1, 0, say.Text), true);
    }
}
