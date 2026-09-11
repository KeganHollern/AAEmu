using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class SkillRequirementsGameData : Singleton<SkillRequirementsGameData>, IGameDataLoader
{
    private sealed class Requirement
    {
        public uint Id { get; init; }
        public bool Target { get; init; }
        public uint BuffId { get; init; }
        public uint BuffTagId { get; init; }
        public bool DefaultResult { get; init; }
        public HashSet<uint> Skills { get; } = [];
        public HashSet<uint> SkillTags { get; } = [];
    }

    private Requirement[] _requirements = [];

    public void Load(SqliteConnection connection)
    {
        var requirements = new Dictionary<uint, Requirement>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, target, buff_id, buff_tag_id, default_result FROM skill_reqs ORDER BY id";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var requirement = new Requirement
                {
                    Id = reader.GetUInt32("id"),
                    Target = reader.GetString("target") is "t" or "T" or "1",
                    BuffId = reader.GetUInt32("buff_id", 0),
                    BuffTagId = reader.GetUInt32("buff_tag_id", 0),
                    DefaultResult = reader.GetString("default_result") is "t" or "T" or "1"
                };
                requirements.Add(requirement.Id, requirement);
            }
        }

        LoadRelations(connection, requirements, "skill_req_skills", "skill_id", false);
        LoadRelations(connection, requirements, "skill_req_skill_tags", "skill_tag_id", true);
        _requirements = requirements.Values.ToArray();
    }

    private static void LoadRelations(SqliteConnection connection, Dictionary<uint, Requirement> requirements,
        string table, string column, bool tags)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT skill_req_id, {column} FROM {table}";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var requirementId = reader.GetUInt32("skill_req_id");
            if (!requirements.TryGetValue(requirementId, out var requirement))
                throw new InvalidDataException($"{table} references missing skill requirement {requirementId}.");
            (tags ? requirement.SkillTags : requirement.Skills).Add(reader.GetUInt32(column));
        }
    }

    public void PostLoad() { }

    public uint GetFailedRequirement(SkillTemplate skill, BaseUnit caster, BaseUnit target)
    {
        if (_requirements.Length == 0)
            return 0;
        return GetFailedRequirement(skill.Id, skill.TargetType, SkillManager.Instance.GetSkillTags(skill.Id), caster, target);
    }

    internal uint GetFailedRequirement(uint skillId, SkillTargetType targetType, IReadOnlyCollection<uint> skillTags,
        BaseUnit caster, BaseUnit target)
    {
        foreach (var requirement in _requirements)
        {
            // r208022 CheckSkillRequirements skips target rules for non-unit target types.
            if (requirement.Target && !ChecksTargetBuffs(targetType))
                continue;
            var subject = requirement.Target ? target : caster;
            if (subject == null)
                continue;

            var listed = requirement.Skills.Contains(skillId) || requirement.SkillTags.Overlaps(skillTags);
            // The lists are exceptions to default_result, not requirements to possess a buff.
            // true is a denylist while the buff is active. false is an allowlist.
            if (listed != requirement.DefaultResult)
                continue;
            if ((requirement.BuffId != 0 && subject.Buffs.CheckBuff(requirement.BuffId)) ||
                (requirement.BuffTagId != 0 && subject.Buffs.CheckBuffTag(requirement.BuffTagId)))
                return requirement.Id;
        }
        return 0;
    }

    internal static bool ChecksTargetBuffs(SkillTargetType targetType)
    {
        return targetType is not (SkillTargetType.Pos or SkillTargetType.Line or SkillTargetType.Doodad or
            SkillTargetType.Item or SkillTargetType.BallisticPos or SkillTargetType.SummonPos or
            SkillTargetType.RelativePos or SkillTargetType.SourcePos or SkillTargetType.ArtilleryPos or SkillTargetType.CursorPos);
    }
}
