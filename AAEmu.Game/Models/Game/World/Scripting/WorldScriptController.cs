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

        if (action.CastSkill != null)
        {
            if (action.CastSkill.DelaySeconds > 0)
                TaskManager.Instance.Schedule(new Tasks.World.WorldScriptCastSkillTask(_world, action.CastSkill),
                    TimeSpan.FromSeconds(action.CastSkill.DelaySeconds));
            else
                CastNow(_world, action.CastSkill);
        }

        if (action.RunCommandSet != null)
        {
            if (action.RunCommandSet.DelaySeconds > 0)
                TaskManager.Instance.Schedule(new Tasks.World.WorldScriptCommandSetTask(_world, action.RunCommandSet),
                    TimeSpan.FromSeconds(action.RunCommandSet.DelaySeconds));
            else
                RunCommandSetNow(_world, action.RunCommandSet);
        }
    }

    /// <summary>
    /// Plays a retail ai_command_sets sequence on a live NPC by applying a fresh
    /// <see cref="Skills.Effects.NpcControlEffect"/> in RunCommandSet mode — the same code path
    /// retail's own trigger skills use, minus their KillNpcWithoutCorpse payloads (those name
    /// spawn-slave NPCs this server does not spawn, and the engine's implementation vanishes the
    /// caster regardless of the named victim). A fresh instance is used deliberately: the
    /// DB-loaded effect templates keep per-cast state in instance fields. (aaemu-cluster#92)
    /// </summary>
    internal static void RunCommandSetNow(WorldInstance world, WorldScriptCommandSet run)
    {
        var npc = FindNpc(world, run.NpcTemplateId, run.Near);
        if (npc == null || npc.IsDead)
        {
            Logger.Warn($"World script RunCommandSet: npc {run.NpcTemplateId} not alive in {world.Template?.Name} ({world.Id})");
            return;
        }

        var commands = GameData.AiGameData.Instance.GetAiCommands(run.CommandSetId);
        if (commands is not { Count: > 0 })
        {
            Logger.Warn($"World script RunCommandSet: ai_command_sets {run.CommandSetId} is empty or missing");
            return;
        }

        var control = new Skills.Effects.NpcControlEffect
        {
            CategoryId = AI.Enums.NpcControlCategory.RunCommandSet,
            ParamInt = run.CommandSetId
        };
        control.Apply(npc, null, npc, null, null, null, null, DateTime.UtcNow);
        Logger.Info($"World script RunCommandSet: npc {run.NpcTemplateId} running set {run.CommandSetId} ({commands.Count} command(s)) in {world.Template?.Name} ({world.Id})");
    }

    /// <summary>
    /// Shows a chat bubble above a live NPC of the template. Fails soft when the NPC is not
    /// (yet) in the world — event-spawned NPCs appear on the next world tick, which is why
    /// scripted lines usually carry a small DelaySeconds. (aaemu-cluster#92 validation)
    /// </summary>
    internal static void SayNow(WorldInstance world, WorldScriptSay say)
    {
        var npc = FindNpc(world, say.NpcTemplateId, say.Near);
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

    /// <summary>
    /// Makes a live NPC cast a skill on itself, the way <see cref="AI.v2.Behaviors.Common.SpawningBehavior"/>
    /// runs OnSpawn skills. Retail's scripted sequences hang off skills like this: the skill's
    /// NpcControlEffect RunCommandSet hands an ai_command_sets row to the AI, which then plays the
    /// set's lines, pauses, walks and self-despawn in retail's own order. (aaemu-cluster#92)
    /// </summary>
    internal static void CastNow(WorldInstance world, WorldScriptCastSkill cast)
    {
        var npc = FindNpc(world, cast.NpcTemplateId, cast.Near);
        if (npc == null || npc.IsDead)
        {
            Logger.Warn($"World script CastSkill: npc {cast.NpcTemplateId} not alive in {world.Template?.Name} ({world.Id})");
            return;
        }

        var skillTemplate = SkillManager.Instance.GetSkillTemplate(cast.SkillId);
        if (skillTemplate == null)
        {
            Logger.Warn($"World script CastSkill: skill {cast.SkillId} does not exist");
            return;
        }

        var caster = Skills.SkillCaster.GetByType(Skills.SkillCasterType.Unit);
        caster.ObjId = npc.ObjId;
        var target = Skills.SkillCastTarget.GetByType(Skills.SkillCastTargetType.Unit);
        target.ObjId = npc.ObjId;

        new Skills.Skill(skillTemplate).Use(npc, caster, target, null, true, out _);
        Logger.Info($"World script CastSkill: npc {cast.NpcTemplateId} cast {cast.SkillId} in {world.Template?.Name} ({world.Id})");
    }

    /// <summary>
    /// Picks the NPC that acts. With a Near filter, the live NPC of the template closest to that
    /// point wins — a template with several placements (the four Sharpwind researchers) would
    /// otherwise be chosen arbitrarily, possibly out of the player's view. (aaemu-cluster#92)
    /// </summary>
    private static NPChar.Npc FindNpc(WorldInstance world, uint templateId, WorldScriptArea near)
    {
        if (near == null)
            return world.GetNpcByTemplateId(templateId);

        var center = new Vector3(near.X, near.Y, near.Z);
        NPChar.Npc best = null;
        var bestDistance = float.MaxValue;
        foreach (var npc in world.GetAllNpcs())
        {
            if (npc.TemplateId != templateId || npc.IsDead)
                continue;

            var distance = Vector3.DistanceSquared(npc.Transform?.World?.Position ?? Vector3.Zero, center);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = npc;
        }

        return best;
    }
}
