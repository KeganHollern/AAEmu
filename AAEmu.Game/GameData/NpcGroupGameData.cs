using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class NpcGroupGameData : Singleton<NpcGroupGameData>, IGameDataLoader
{
    private sealed record Snapshot(Dictionary<int, NpcGroup> Groups,
        Dictionary<int, Dictionary<int, NpcGroupMember>> Members);

    private Snapshot _snapshot = new([], []);

    public NpcGroup GetNpcGroup(int id)
    {
        Volatile.Read(ref _snapshot).Groups.TryGetValue(id, out var npcGroup);
        return npcGroup;
    }

    public List<NpcGroupMember> GetNpcGroupMembers(int npcGroupId)
    {
        if (Volatile.Read(ref _snapshot).Members.TryGetValue(npcGroupId, out var members))
        {
            return members.Values.OrderBy(member => member.Id).ToList();
        }
        return [];
    }

    public NpcGroupMember GetNpcGroupMember(int npcGroupId, int memberId)
    {
        if (Volatile.Read(ref _snapshot).Members.TryGetValue(npcGroupId, out var members))
        {
            members.TryGetValue(memberId, out var member);
            return member;
        }
        return null;
    }

    public void Load(SqliteConnection connection)
    {
        var groups = LoadNpcGroups(connection);
        var members = LoadNpcGroupMembers(connection);
        Volatile.Write(ref _snapshot, new Snapshot(groups, members));
    }

    private static Dictionary<int, NpcGroup> LoadNpcGroups(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var groups = new Dictionary<int, NpcGroup>();
        command.CommandText = "SELECT * FROM npc_groups";
        command.Prepare();
        using var sqliteReader = command.ExecuteReader();
        using var reader = new SQLiteWrapperReader(sqliteReader);
        while (reader.Read())
        {
            var template = new NpcGroup();
            template.Id = reader.GetInt32("id");
            template.Name = reader.GetString("name");
            template.AggroRuleId = reader.GetInt32("aggro_rule_id");
            template.EnableRespawn = reader.GetBoolean("enable_respawn", true);

            groups.Add(template.Id, template);
        }
        return groups;
    }

    private static Dictionary<int, Dictionary<int, NpcGroupMember>> LoadNpcGroupMembers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var groups = new Dictionary<int, Dictionary<int, NpcGroupMember>>();
        command.CommandText = "SELECT * FROM npc_group_members";
        command.Prepare();
        using var sqliteReader = command.ExecuteReader();
        using var reader = new SQLiteWrapperReader(sqliteReader);
        while (reader.Read())
        {
            var template = new NpcGroupMember();
            template.Id = reader.GetInt32("id");
            template.NpcGroupId = reader.GetInt32("npc_group_id");
            template.NpcId = reader.GetInt32("npc_id");
            template.IsLeader = reader.GetBoolean("is_leader", true);
            template.IsMoveLeader = reader.GetBoolean("is_move_leader", true);
            template.FormationOffsetX = reader.GetFloat("formation_offset_x");
            template.FormationOffsetY = reader.GetFloat("formation_offset_y");
            template.FormationOffsetZ = reader.GetFloat("formation_offset_z");
            template.FormationTension = reader.GetFloat("formation_tension");

            if (!groups.TryGetValue(template.NpcGroupId, out var members))
                groups.Add(template.NpcGroupId, members = []);
            members.Add(template.Id, template);
        }
        return groups;
    }

    public void PostLoad()
    {
    }
}
