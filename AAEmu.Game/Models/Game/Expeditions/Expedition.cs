using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Expeditions;

public class Expedition : SystemFaction
{
    private readonly List<uint> _removedMembers = [];

    public List<ExpeditionMember> Members { get; set; } = [];
    public List<ExpeditionRolePolicy> Policies { get; set; } = [];

    public bool isDisbanded { get; set; } = false;

    public bool IsOwner(Character character) =>
        !isDisbanded && character != null && OwnerId == character.Id && GetMember(character)?.Role == 255;

    public bool CanManageMember(ExpeditionMember actor, ExpeditionMember target) =>
        !isDisbanded && actor != null && target != null && target.CharacterId != OwnerId &&
        target.Role != 255 && actor.Role > target.Role;

    public bool CanChat(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var member = character == null || !ReferenceEquals(character.Expedition, this) ? null : GetMember(character);
            return !isDisbanded && member != null && GetPolicyByRole(member.Role)?.Chat == true;
        }
    }

    public void RemoveMember(ExpeditionMember member)
    {
        Members.Remove(member);
        _removedMembers.Add(member.CharacterId);
    }

    internal void RestoreMember(ExpeditionMember member, int index)
    {
        Members.Insert(index, member);
        _removedMembers.Remove(member.CharacterId);
    }

    public void OnCharacterLogin(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!ReferenceEquals(character?.Expedition, this))
                return;
            var member = GetMember(character);
            if (isDisbanded || member == null)
            {
                character.Expedition = null;
                character.BroadcastPacket(new SCUnitExpeditionChangedPacket(character.ObjId, character.Id,
                    "", character.Name, (uint)Id, 0, false), true);
                return;
            }

            member.Refresh(character);
            SendPacket(new SCExpeditionMemberStatusChangedPacket(member, 0));
            ChatManager.Instance.GetGuildChat(this).JoinChannel(character);
            character.Achievements.UpdateMaximum(CharRecordKind.EnrollGuild, 0, 0, 1);
        }
    }

    public void OnCharacterLogout(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (isDisbanded || !ReferenceEquals(character?.Expedition, this))
                return;
            var member = GetMember(character);
            if (member != null)
            {
                member.IsOnline = false;
                member.LastWorldLeaveTime = DateTime.UtcNow;
                SendPacket(new SCExpeditionMemberStatusChangedPacket(member, 0));
            }
            ChatManager.Instance.GetGuildChat(this).LeaveChannel(character);
        }
    }

    public ExpeditionRolePolicy GetPolicyByRole(byte role)
    {
        foreach (var policy in Policies)
            if (policy.Role == role)
                return policy;

        return null;
    }

    public ExpeditionMember GetMember(Character character)
    {
        foreach (var member in Members)
            if (member.CharacterId == character.Id)
                return member;
        return null;
    }

    public ExpeditionMember GetMember(uint characterId)
    {
        foreach (var member in Members)
            if (member.CharacterId == characterId)
                return member;
        return null;
    }

    public void SendPacket(GamePacket packet)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var member in Members)
                WorldManager.Instance.GetCharacterById(member.CharacterId)?.SendPacket(packet);
        }
    }

    public void Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        if (_removedMembers.Count > 0)
        {
            var removedMembers = string.Join(",", _removedMembers);

            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;

                command.CommandText = $"DELETE FROM expedition_members WHERE character_id IN ({removedMembers})";
                command.Prepare();
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;

                command.CommandText = $"UPDATE characters SET expedition_id = 0 WHERE `characters`.`id` IN ({removedMembers})";
                command.Prepare();
                command.ExecuteNonQuery();
            }
        }

        if (isDisbanded)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE characters SET expedition_id = 0 WHERE expedition_id = @id";
                command.Parameters.AddWithValue("@id", Id);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM expeditions WHERE `id` = @id";
                command.Parameters.AddWithValue("@id", this.Id);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM expedition_role_policies WHERE `expedition_id` = @id";
                command.Parameters.AddWithValue("@id", this.Id);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM expedition_members WHERE `expedition_id` = @id";
                command.Parameters.AddWithValue("@id", this.Id);
                command.ExecuteNonQuery();
            }
        }
        else
        {
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;

                command.CommandText =
                    "REPLACE INTO expeditions(`id`,`owner`,`owner_name`,`name`,`mother`,`created_at`) VALUES (@id, @owner, @owner_name, @name, @mother, @created_at)";
                command.Parameters.AddWithValue("@id", this.Id);
                command.Parameters.AddWithValue("@owner", this.OwnerId);
                command.Parameters.AddWithValue("@owner_name", this.OwnerName);
                command.Parameters.AddWithValue("@name", this.Name);
                command.Parameters.AddWithValue("@mother", this.MotherId);
                command.Parameters.AddWithValue("@created_at", this.Created);
                command.ExecuteNonQuery();
            }

            foreach (var member in Members)
                member.Save(connection, transaction);

            foreach (var policy in Policies)
                policy.Save(connection, transaction);
        }
    }

    internal void OnSaved() => _removedMembers.Clear();

    public void OnCharacterRefresh(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (isDisbanded || !ReferenceEquals(character?.Expedition, this))
                return;
            var member = GetMember(character);
            if (member == null)
                return;
            member.Refresh(character);
            SendPacket(new SCExpeditionMemberStatusChangedPacket(member, 0));
        }
    }
}
