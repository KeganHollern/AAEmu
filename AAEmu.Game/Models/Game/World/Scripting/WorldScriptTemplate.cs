using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;

using NLog;

namespace AAEmu.Game.Models.Game.World.Scripting;

/// <summary>
/// Data-driven world/dungeon event scripts, loaded from
/// Data/Worlds/{world}/dungeon_scripts.json.
///
/// Retail scripted dungeons like Sharpwind Mines (aaemu-cluster#92) chain
/// doodad phases, event spawners, and area triggers with server-side glue that
/// XLGames never shipped in the compact database (the doodad logic-family
/// tables describe the pieces but not the wiring, and the dungeon has no
/// indun_events rows). These scripts supply that wiring explicitly per world:
/// when a doodad reaches a phase / all doodads of a template reach a phase /
/// a player enters an area, run actions (activate, deactivate, or despawn NPC
/// spawners; change another doodad's phase).
/// </summary>
public class WorldScriptRule
{
    /// <summary>Human-readable rule name for logs; not used by code.</summary>
    public string Name { get; set; }

    /// <summary>Fires when a doodad of the template enters the func group.</summary>
    public WorldScriptDoodadPhase OnDoodadPhase { get; set; }

    /// <summary>Fires when EVERY live doodad of the template is in one of the func groups.</summary>
    public WorldScriptAllDoodadsPhase OnAllDoodadsPhase { get; set; }

    /// <summary>Fires when any player is within Radius of the point.</summary>
    public WorldScriptArea OnPlayerEnterArea { get; set; }

    /// <summary>Fires when an NPC of one of the templates dies in this world (aaemu-cluster#92: retail boss chains).</summary>
    public WorldScriptNpcKilled OnNpcKilled { get; set; }

    /// <summary>Actions executed, in order, when the condition first holds.</summary>
    public List<WorldScriptAction> Actions { get; set; } = [];
}

public class WorldScriptDoodadPhase
{
    public uint DoodadTemplateId { get; set; }
    public uint FuncGroupId { get; set; }

    /// <summary>
    /// Optional spatial filter: only doodads within Radius of the point match. Needed when one
    /// template has several placements with different roles (e.g. the two Sharpwind powder-keg
    /// clusters each opening their own Rock wall). Null/zero radius = any placement.
    /// </summary>
    public WorldScriptArea Near { get; set; }
}

public class WorldScriptNpcKilled
{
    public List<uint> NpcTemplateIds { get; set; } = [];
}

public class WorldScriptAllDoodadsPhase
{
    public uint DoodadTemplateId { get; set; }
    public List<uint> FuncGroupIds { get; set; } = [];
}

public class WorldScriptArea
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
}

public class WorldScriptAction
{
    /// <summary>npc_spawners template ids to activate (event spawners spawn on the next tick).</summary>
    public List<uint> ActivateNpcSpawners { get; set; }

    /// <summary>npc_spawners template ids to deactivate (stops future spawns; leaves live NPCs).</summary>
    public List<uint> DeactivateNpcSpawners { get; set; }

    /// <summary>npc_spawners template ids to deactivate AND despawn immediately.</summary>
    public List<uint> DespawnNpcSpawners { get; set; }

    /// <summary>Move every live doodad of the template (optionally Near-filtered) to the func group.</summary>
    public WorldScriptDoodadPhase ChangeDoodadPhase { get; set; }

    /// <summary>Spawn doodads at fixed positions (aaemu-cluster#92: retail spawned e.g. the exit portal on the final boss's death).</summary>
    public List<WorldScriptDoodadSpawn> SpawnDoodads { get; set; }

    /// <summary>Show a chat bubble above a live NPC of the template (optionally delayed).</summary>
    public WorldScriptSay Say { get; set; }

    /// <summary>
    /// Make a live NPC cast a skill on itself. This is how retail's own scripted sequences are
    /// driven: the skill carries an NpcControlEffect whose RunCommandSet points at an
    /// ai_command_sets row, and the engine then executes that set's UseSkill / Timeout / FollowPath
    /// commands in order (lines, pauses, walks, self-despawn). Prefer this over hand-timed Say
    /// chains — the choreography, its beat spacing and its localized lines all ship in the compact.
    /// (aaemu-cluster#92)
    /// </summary>
    public WorldScriptCastSkill CastSkill { get; set; }

    /// <summary>
    /// Play a retail ai_command_sets sequence on a live NPC: the set's own UseSkill / Timeout /
    /// FollowPath commands, in retail's order and with retail's beat spacing. Preferred over both
    /// hand-timed Say chains and CastSkill, because the skills that carry these sets in retail also
    /// carry KillNpcWithoutCorpse payloads aimed at spawn-slave NPCs we do not spawn.
    /// (aaemu-cluster#92)
    /// </summary>
    public WorldScriptCommandSet RunCommandSet { get; set; }

    /// <summary>
    /// Defers this whole action after the rule fires. Retail staged some events strictly after a
    /// scripted sequence elsewhere finished (the bridge slimes attack only once cinematic Nerta has
    /// taunted and despawned); the delay expresses that ordering. Runs against the world, so it
    /// no-ops if the instance was torn down meanwhile. (aaemu-cluster#92)
    /// </summary>
    public float DelaySeconds { get; set; }
}

public class WorldScriptCommandSet
{
    public uint NpcTemplateId { get; set; }

    /// <summary>ai_command_sets id (e.g. 185 = 칼바람폐광_알리스테어0, the mine-mouth sequence).</summary>
    public uint CommandSetId { get; set; }

    /// <summary>Optional actor filter when the template has several live placements.</summary>
    public WorldScriptArea Near { get; set; }

    public float DelaySeconds { get; set; }
}

public class WorldScriptCastSkill
{
    public uint NpcTemplateId { get; set; }

    /// <summary>Skill the NPC casts on itself; usually one carrying a RunCommandSet NpcControlEffect.</summary>
    public uint SkillId { get; set; }

    /// <summary>Optional speaker/actor filter when the template has several live placements.</summary>
    public WorldScriptArea Near { get; set; }

    public float DelaySeconds { get; set; }
}

public class WorldScriptDoodadSpawn
{
    public uint TemplateId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
}

/// <summary>
/// A scripted NPC line rendered as a chat bubble above the NPC via SCChatBubblePacket.
/// Prefer BubbleId: it references a retail bubble_effects row, which the client renders in the
/// player's own locale (retail delivered these beats from XL server AI scripts that never
/// shipped; the bubble text itself DID ship in the client). Text is a fallback for beats that
/// have no retail bubble row. (aaemu-cluster#92)
/// </summary>
public class WorldScriptSay
{
    public uint NpcTemplateId { get; set; }

    /// <summary>Retail bubble_effects id; when set, the client shows its own localized line.</summary>
    public uint BubbleId { get; set; }

    /// <summary>Authored fallback text, used only when BubbleId is 0.</summary>
    public string Text { get; set; }

    /// <summary>
    /// Optional speaker filter: with several live NPCs of the template (e.g. the four Sharpwind
    /// researchers), speak from the one nearest this point instead of an arbitrary one.
    /// </summary>
    public WorldScriptArea Near { get; set; }

    public float DelaySeconds { get; set; }
}

public static class WorldScriptTemplate
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static readonly Dictionary<string, List<WorldScriptRule>> Cache = [];
    private static readonly object CacheLock = new();

    /// <summary>
    /// Returns the script rules for a world template, or null when the world has
    /// no dungeon_scripts.json. Parsed once per world template and cached; a
    /// malformed file logs an error and is treated as absent (fail-soft).
    /// </summary>
    public static List<WorldScriptRule> GetForWorld(string worldTemplateName)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(worldTemplateName, out var cached))
                return cached;

            List<WorldScriptRule> rules = null;
            var fileName = Path.Combine(FileManager.AppPath, "Data", "Worlds", worldTemplateName, "dungeon_scripts.json");
            if (File.Exists(fileName))
            {
                var contents = FileManager.GetFileContents(fileName);
                if (string.IsNullOrWhiteSpace(contents))
                {
                    Logger.Warn($"File {fileName} is empty.");
                }
                else if (!JsonHelper.TryDeserializeObject(contents, out rules, out var exception))
                {
                    Logger.Error($"Failed to parse {fileName}: {exception}");
                    rules = null;
                }
                else
                {
                    Logger.Info($"Loaded {rules.Count} world script rule(s) for {worldTemplateName}");
                }
            }

            Cache.Add(worldTemplateName, rules);
            return rules;
        }
    }
}
