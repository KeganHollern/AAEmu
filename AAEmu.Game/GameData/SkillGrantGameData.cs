using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class SkillGrantGameData : Singleton<SkillGrantGameData>, IGameDataLoader
{
    private Dictionary<uint, HashSet<uint>> _skillBuffs = [];
    private Dictionary<uint, HashSet<uint>> _doodadSkills = [];
    private Dictionary<uint, HashSet<uint>> _npcSkills = [];

    public void Load(SqliteConnection connection)
    {
        _skillBuffs = LoadRelations(connection, "SELECT skill_id AS source_id, buff_id AS value_id FROM buff_skills");
        _npcSkills = LoadRelations(connection, "SELECT npc_interaction_set_id AS source_id, skill_id AS value_id FROM npc_interactions");
        _doodadSkills = LoadRelations(connection, """
            SELECT doodad_func_group_id AS source_id, func_skill_id AS value_id FROM doodad_funcs
            UNION
            SELECT f.doodad_func_group_id, u.fake_skill_id
            FROM doodad_funcs f JOIN doodad_func_fake_uses u ON u.id = f.actual_func_id
            WHERE f.actual_func_type = 'DoodadFuncFakeUse'
            UNION
            SELECT f.doodad_func_group_id, u.skill_id
            FROM doodad_funcs f JOIN doodad_func_uses u ON u.id = f.actual_func_id
            WHERE f.actual_func_type = 'DoodadFuncUse'
            """);
    }

    private static Dictionary<uint, HashSet<uint>> LoadRelations(SqliteConnection connection, string query)
    {
        var relations = new Dictionary<uint, HashSet<uint>>();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var source = reader.GetUInt32("source_id", 0);
            var value = reader.GetUInt32("value_id", 0);
            if (source == 0 || value == 0)
                continue;
            if (!relations.TryGetValue(source, out var values))
                relations.Add(source, values = []);
            values.Add(value);
        }
        return relations;
    }

    public void PostLoad() { }

    public bool HasBuffGrant(uint skillId, IBuffs buffs)
    {
        return buffs != null && _skillBuffs.TryGetValue(skillId, out var grants) &&
            buffs.HasEffectsMatchingCondition(buff => buff.InUse && !buff.IsEnded() && buff.Template != null &&
                grants.Contains(buff.Template.BuffId) && (buff.Duration == 0 || buff.GetTimeLeft() > 0));
    }

    public bool HasDoodadGrant(uint phaseId, uint skillId)
    {
        return _doodadSkills.TryGetValue(phaseId, out var skills) && skills.Contains(skillId);
    }

    public bool HasNpcGrant(uint interactionSetId, uint skillId)
    {
        return _npcSkills.TryGetValue(interactionSetId, out var skills) && skills.Contains(skillId);
    }
}
