using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class MateGameData : Singleton<MateGameData>, IGameDataLoader
{
    private Dictionary<uint, NpcMountSkills> _npcMountSkills = [];
    private Dictionary<uint, MountSkills> _mountSkills = [];
    private Dictionary<uint, MountAttachedSkills> _mountAttachedSkills = [];

    private Dictionary<uint, HashSet<uint>> _buffMountSkills = [];
    private Dictionary<int, HashSet<EquipmentItemSlot>> _equipmentSlots = [];
    private HashSet<uint> _underwaterModels = [];

    public bool IsUnderwaterModel(uint modelId) => _underwaterModels.Contains(modelId);

    public bool HasEquipmentSlot(int packId, int slot) =>
        _equipmentSlots.TryGetValue(packId, out var slots) && slots.Contains((EquipmentItemSlot)slot);

    public bool HasAttachedSeat(IEnumerable<uint> mountSkillIds, AttachPointKind seat) =>
        _mountAttachedSkills.Values.Any(row => row.AttachPointId == seat && mountSkillIds.Contains(row.MountSkillId));

    public uint GetSkillId(uint mountSkillId) => _mountSkills.GetValueOrDefault(mountSkillId)?.SkillId ?? 0;

    public bool IsMountSkillBuff(uint buffId) => _buffMountSkills.ContainsKey(buffId);

    public bool BuffGrantsMountSkill(uint buffId, uint mountSkillId) =>
        _buffMountSkills.TryGetValue(buffId, out var skills) && skills.Contains(mountSkillId);

    public bool TryGetRiderSkill(uint mountSkillId, AttachPointKind seat, out uint skillId)
    {
        skillId = 0;
        var attached = _mountAttachedSkills.Values.Where(row => row.MountSkillId == mountSkillId).ToArray();
        if (attached.Length == 0)
            return seat is AttachPointKind.None or AttachPointKind.Driver;
        var match = attached.FirstOrDefault(row => row.AttachPointId == seat);
        if (match == null)
            return false;
        skillId = match.SkillId;
        return true;
    }

    /// <summary>
    /// Gets a list of pet skill Ids
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public List<uint> GetMateSkills(uint id)
    {
        var template = new List<uint>();

        foreach (var value in _npcMountSkills.Values)
            if (value.NpcId == id && !template.Contains(value.MountSkillId))
                template.Add(value.MountSkillId);

        return template;
    }

    /// <summary>
    /// Get the associated rider skill for a given mountSkill
    /// </summary>
    /// <param name="mateSkill">The skill the mate used</param>
    /// <param name="attachPoint">The attachPoint the player is currently on</param>
    /// <returns></returns>
    public uint GetMountAttachedSkills(uint mateSkill, AttachPointKind attachPoint)
    {
        var id = 0u;
        var skill = 0u;

        // Find the mountSkillId for this mate's skill
        foreach (var ms in _mountSkills)
        {
            if (ms.Value.SkillId != mateSkill)
                continue;
            id = ms.Key;
            break;
        }

        // Find the player skill based on the mountSkillId
        foreach (var mas in _mountAttachedSkills)
        {
            if (mas.Value.MountSkillId != id || mas.Value.AttachPointId != attachPoint)
                continue;
            skill = mas.Value.SkillId;
            break;
        }

        return skill;
    }

    /// <summary>
    /// Gets MountSkillId for use with Slaves
    /// </summary>
    /// <param name="slaveSkillId"></param>
    /// <returns></returns>
    public uint GetMountSkillIdForSkill(uint slaveSkillId)
    {
        foreach (var ms in _mountSkills.Values)
        {
            if (ms.SkillId == slaveSkillId)
                return ms.Id;
        }

        return 0;
    }

    /// <summary>
    /// Loads the game db data for pets
    /// </summary>
    /// <param name="connection"></param>
    public void Load(SqliteConnection connection)
    {
        _npcMountSkills = [];
        _mountSkills = [];
        _mountAttachedSkills = [];
        _buffMountSkills = [];
        _equipmentSlots = [];
        _underwaterModels = [];

        #region MateTables

        // Npc Mount skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM npc_mount_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new NpcMountSkills
                    {
                        Id = reader.GetUInt32("id"),
                        NpcId = reader.GetUInt32("npc_id"),
                        MountSkillId = reader.GetUInt32("mount_skill_id")
                    };
                    _npcMountSkills.Add(template.Id, template);
                }
            }
        }

        // Mount Skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mount_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new MountSkills
                    {
                        Id = reader.GetUInt32("id"),
                        Name = reader.GetString("name", ""),
                        SkillId = reader.GetUInt32("skill_id")
                    };
                    _mountSkills.Add(template.Id, template);
                }
            }
        }

        // Mount attached skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mount_attached_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new MountAttachedSkills
                    {
                        Id = reader.GetUInt32("id"),
                        MountSkillId = reader.GetUInt32("mount_skill_id"),
                        AttachPointId = (AttachPointKind)reader.GetUInt32("attach_point_id"),
                        SkillId = reader.GetUInt32("skill_id")
                    };
                    _mountAttachedSkills.Add(template.Id, template);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT buff_id, mount_skill_id FROM buff_mount_skills";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var buffId = reader.GetUInt32("buff_id");
                if (!_buffMountSkills.TryGetValue(buffId, out var skills))
                    _buffMountSkills.Add(buffId, skills = []);
                skills.Add(reader.GetUInt32("mount_skill_id"));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mate_equip_slot_packs";
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
            {
                var slots = new HashSet<EquipmentItemSlot>();
                foreach (var (column, slot) in new[]
                {
                    ("head", EquipmentItemSlot.Head), ("chest", EquipmentItemSlot.Chest),
                    ("waist", EquipmentItemSlot.Waist), ("feet", EquipmentItemSlot.Feet)
                })
                    if (reader.GetBoolean(column, true))
                        slots.Add(slot);
                _equipmentSlots.Add(reader.GetInt32("id"), slots);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.id FROM models m JOIN actor_models a ON a.id = m.sub_id
                WHERE m.sub_type = 'ActorModel' AND a.underwater_creature = 't'
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                _underwaterModels.Add((uint)reader.GetInt64(0));
        }

        #endregion MateTables
    }

    public void PostLoad()
    {
        // Nothing to do here
    }
}
