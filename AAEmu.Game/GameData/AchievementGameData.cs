using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class AchievementGameData : Singleton<AchievementGameData>, IGameDataLoader
{
    private Dictionary<uint, CharRecords> _charRecords = [];
    private Dictionary<uint, Achievements> _achievements = [];
    private Dictionary<uint, List<AchievementObjectives>> _achievementObjectives = [];
    private Dictionary<uint, List<PreCompletedAchievements>> _preCompletedAchievements = [];
    private Dictionary<(CharRecordKind Kind, uint Value1, uint Value2), CharRecords> _charRecordsByKind = [];
    private Dictionary<(CharRecordKind Kind, uint Value1), List<CharRecords>> _charRecordsByKindAndValue1 = [];
    private Dictionary<uint, List<uint>> _achievementIdsByRecord = [];
    private Dictionary<uint, List<uint>> _achievementIdsByPrerequisite = [];
    private List<uint> _activeAchievementIds = [];

    public void Load(SqliteConnection connection)
    {
        _charRecords = [];
        _achievements = [];
        _achievementObjectives = [];
        _preCompletedAchievements = [];

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM achievements";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var template = new Achievements
                    {
                        Id = reader.GetUInt32("id"),
                        CategoryId = reader.GetUInt32("category_id", 0),
                        CompleteNum = reader.GetUInt32("complete_num", 0),
                        CompleteOr = reader.GetBoolean("complete_or", true),
                        IconId = reader.GetUInt32("icon_id", 0),
                        IsActive = reader.GetBoolean("is_active", true),
                        IsHidden = reader.GetBoolean("is_hidden", true),
                        ItemId = reader.GetUInt32("item_id", 0),
                        OrUnitReqs = reader.GetBoolean("or_unit_reqs", true),
                        ParentAchievementId = reader.GetUInt32("parent_achievement_id", 0),
                        Priority = reader.GetUInt32("priority", 0),
                        SubCategoryId = reader.GetUInt32("sub_category_id", 0)
                    };

                    _achievements.TryAdd(template.Id, template);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM achievement_objectives";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var template = new AchievementObjectives
                    {
                        Id = reader.GetUInt32("id"),
                        AchievementId = reader.GetUInt32("achievement_id"),
                        OrUnitReqs = reader.GetBoolean("or_unit_reqs", true),
                        RecordId = reader.GetUInt32("record_id")
                    };

                    if (!_achievementObjectives.TryGetValue(template.AchievementId, out var value))
                    {
                        value = [];
                        _achievementObjectives.Add(template.AchievementId, value);
                    }

                    value.Add(template);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM pre_completed_achievements";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var template = new PreCompletedAchievements
                    {
                        Id = reader.GetUInt32("id"), CompletedAchievementId = reader.GetUInt32("completed_achievement_id"),
                        MyAchievementId = reader.GetUInt32("my_achievement_id")
                    };

                    if (!_preCompletedAchievements.TryGetValue(template.MyAchievementId, out var value))
                    {
                        value = [];
                        _preCompletedAchievements.Add(template.MyAchievementId, value);
                    }

                    value.Add(template);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM char_records";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var template = new CharRecords
                    {
                        Id = reader.GetUInt32("id"),
                        KindId = (CharRecordKind)reader.GetUInt32("kind_id"),
                        Value1 = reader.GetUInt32("value1"),
                        Value2 = reader.GetUInt32("value2")
                    };

                    _charRecords.Add(template.Id, template);
                }
            }
        }
    }

    public void PostLoad()
    {
        _charRecordsByKind = _charRecords.Values.ToDictionary(
            record => (record.KindId, record.Value1, record.Value2));
        _charRecordsByKindAndValue1 = _charRecords.Values
            .GroupBy(record => (record.KindId, record.Value1))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(record => record.Value2).ThenBy(record => record.Id).ToList());
        _achievementIdsByRecord = [];
        _achievementIdsByPrerequisite = [];
        _activeAchievementIds = _achievements.Values
            .Where(achievement => achievement.IsActive)
            .Select(achievement => achievement.Id)
            .Order()
            .ToList();

        foreach (var objectives in _achievementObjectives.Values)
        {
            objectives.Sort((left, right) => left.Id.CompareTo(right.Id));
            foreach (var objective in objectives)
            {
                if (!_achievementIdsByRecord.TryGetValue(objective.RecordId, out var achievementIds))
                {
                    achievementIds = [];
                    _achievementIdsByRecord.Add(objective.RecordId, achievementIds);
                }

                achievementIds.Add(objective.AchievementId);
            }
        }

        foreach (var prerequisites in _preCompletedAchievements.Values)
        {
            prerequisites.Sort((left, right) => left.Id.CompareTo(right.Id));
            foreach (var prerequisite in prerequisites)
            {
                if (!_achievementIdsByPrerequisite.TryGetValue(prerequisite.CompletedAchievementId, out var achievementIds))
                {
                    achievementIds = [];
                    _achievementIdsByPrerequisite.Add(prerequisite.CompletedAchievementId, achievementIds);
                }

                achievementIds.Add(prerequisite.MyAchievementId);
            }
        }

        foreach (var achievementIds in _achievementIdsByRecord.Values)
            achievementIds.Sort();
        foreach (var achievementIds in _achievementIdsByPrerequisite.Values)
            achievementIds.Sort();
    }

    public IReadOnlyList<uint> GetActiveAchievementIds()
    {
        return _activeAchievementIds;
    }

    public bool TryGetAchievement(uint achievementId, out Achievements achievement)
    {
        return _achievements.TryGetValue(achievementId, out achievement);
    }

    public bool TryGetCharRecord(CharRecordKind kind, uint value1, uint value2, out CharRecords record)
    {
        return _charRecordsByKind.TryGetValue((kind, value1, value2), out record);
    }

    public IReadOnlyList<CharRecords> GetMatchingCharRecords(
        CharRecordKind kind,
        uint value1,
        uint value2,
        bool matchValue2Wildcard = false)
    {
        var hasExactRecord = _charRecordsByKind.TryGetValue((kind, value1, value2), out var exactRecord);
        if (!matchValue2Wildcard || value2 == uint.MaxValue)
            return hasExactRecord ? [exactRecord] : [];

        var hasWildcardRecord = _charRecordsByKind.TryGetValue((kind, value1, uint.MaxValue), out var wildcardRecord);
        if (hasExactRecord && hasWildcardRecord)
            return [exactRecord, wildcardRecord];
        if (hasExactRecord)
            return [exactRecord];
        if (hasWildcardRecord)
            return [wildcardRecord];
        return [];
    }

    public IReadOnlyList<CharRecords> GetCharRecords(CharRecordKind kind, uint value1)
    {
        return _charRecordsByKindAndValue1.GetValueOrDefault((kind, value1)) ?? [];
    }

    public IReadOnlyList<AchievementObjectives> GetObjectives(uint achievementId)
    {
        return _achievementObjectives.GetValueOrDefault(achievementId) ?? [];
    }

    public IReadOnlyList<PreCompletedAchievements> GetPrerequisites(uint achievementId)
    {
        return _preCompletedAchievements.GetValueOrDefault(achievementId) ?? [];
    }

    public IReadOnlyList<uint> GetAchievementIdsForRecord(uint recordId)
    {
        return _achievementIdsByRecord.GetValueOrDefault(recordId) ?? [];
    }

    public IReadOnlyList<uint> GetAchievementIdsUnlockedBy(uint completedAchievementId)
    {
        return _achievementIdsByPrerequisite.GetValueOrDefault(completedAchievementId) ?? [];
    }
}
