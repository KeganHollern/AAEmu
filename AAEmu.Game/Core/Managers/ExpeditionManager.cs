using System.Numerics;
using System.Text.RegularExpressions;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public class ExpeditionManager(IExpeditionIdManager expeditionIdManager, ITeamManager teamManager, IWorldManager worldManager, IChatManager chatManager) : Singleton<ExpeditionManager>, IExpeditionManager
{
    //private ExpeditionConfig _config;
    private Regex _nameRegex;

    private Dictionary<FactionsEnum, Expedition> _expeditions = [];
    private static readonly object s_syncRoot = SaveManager.PersistenceSyncRoot;
    internal Action<Expedition> PersistExpedition { get; set; } = Save;
    // Custom Origins policy. The client does not define the server membership limit.
    internal const int MemberLimit = 100;
    private readonly Dictionary<uint, PendingInvitation> _pendingInvitations = [];
    internal Func<DateTime> InvitationTime { get; set; } = () => DateTime.UtcNow;
    private sealed record PendingInvitation(Character Inviter, GameConnection InviterConnection,
        Character Invited, GameConnection InvitedConnection, Expedition Expedition, DateTime Expires);

    public IEnumerable<Expedition> Expeditions
    {
        get
        {
            lock (s_syncRoot)
                return _expeditions.Values.ToArray();
        }
    }

    private Expedition Create(string name, Character owner)
    {
        var expedition = new Expedition
        {
            Id = (FactionsEnum)expeditionIdManager.GetNextId(),
            MotherId = owner.Faction.Id,
            Name = name,
            OwnerId = owner.Id,
            OwnerName = owner.Name,
            UnitOwnerType = 0,
            PoliticalSystem = 1,
            Created = DateTime.UtcNow,
            AggroLink = false,
            DiplomacyTarget = false,
            Members = []
        };
        expedition.Policies = GetDefaultPolicies(expedition.Id);

        var member = GetMemberFromCharacter(expedition, owner, true);

        expedition.Members.Add(member);

        return expedition;
    }

    public void Load()
    {
        _expeditions = [];
        _nameRegex = new Regex(AppConfiguration.Instance.Expedition.NameRegex, RegexOptions.Compiled);

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM expeditions";
                command.Prepare();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var expedition = new Expedition
                        {
                            Id = (FactionsEnum)reader.GetUInt32("id"),
                            MotherId = (FactionsEnum)reader.GetUInt32("mother"),
                            Name = reader.GetString("name"),
                            OwnerId = reader.GetUInt32("owner"),
                            OwnerName = reader.GetString("owner_name"),
                            UnitOwnerType = 0,
                            PoliticalSystem = 1,
                            Created = reader.GetDateTime("created_at"),
                            AggroLink = false,
                            DiplomacyTarget = false
                        };

                        _expeditions.Add(expedition.Id, expedition);
                    }
                }
            }

            foreach (var expedition in _expeditions.Values)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM expedition_members WHERE expedition_id = @expedition_id";
                    command.Parameters.AddWithValue("@expedition_id", expedition.Id);
                    command.Prepare();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var member = new ExpeditionMember
                            {
                                CharacterId = reader.GetUInt32("character_id"),
                                ExpeditionId = (FactionsEnum)reader.GetUInt32("expedition_id"),
                                Role = reader.GetByte("role"),
                                Memo = reader.GetString("memo"),
                                LastWorldLeaveTime = reader.GetDateTime("last_leave_time"),
                                Name = reader.GetString("name"),
                                Level = reader.GetByte("level"),
                                Abilities =
                                [
                                    reader.GetByte("ability1"), reader.GetByte("ability2"), reader.GetByte("ability3")
                                ],
                                IsOnline = false,
                                InParty = false
                            };
                            expedition.Members.Add(member);
                        }
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT * FROM expedition_role_policies WHERE expedition_id = @expedition_id";
                    command.Parameters.AddWithValue("@expedition_id", expedition.Id);
                    command.Prepare();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var policy = new ExpeditionRolePolicy
                            {
                                ExpeditionId = (FactionsEnum)reader.GetUInt32("expedition_id"),
                                Role = reader.GetByte("role"),
                                Name = reader.GetString("name"),
                                DominionDeclare = reader.GetBoolean("dominion_declare"),
                                Invite = reader.GetBoolean("invite"),
                                Expel = reader.GetBoolean("expel"),
                                Promote = reader.GetBoolean("promote"),
                                Dismiss = reader.GetBoolean("dismiss"),
                                Chat = reader.GetBoolean("chat"),
                                ManagerChat = reader.GetBoolean("manager_chat"),
                                SiegeMaster = reader.GetBoolean("siege_master"),
                                JoinSiege = reader.GetBoolean("join_siege")
                            };
                            expedition.Policies.Add(policy);
                        }
                    }
                }
            }
        }
    }

    public static List<ExpeditionRolePolicy> GetDefaultPolicies(FactionsEnum expeditionId)
    {
        var res = new List<ExpeditionRolePolicy>();
        foreach (var rolePolicy in AppConfiguration.Instance.Expedition.RolePolicies)
        {
            var policy = rolePolicy.Clone();
            policy.ExpeditionId = expeditionId;
            res.Add(policy);
        }

        return res;
    }

    public Expedition GetExpedition(FactionsEnum id)
    {
        lock (s_syncRoot)
            return _expeditions.GetValueOrDefault(id);
    }

    public void CreateExpedition(string name, GameConnection connection)
    {
        lock (s_syncRoot)
        {
            var owner = connection.ActiveChar;
            if (owner.Expedition != null)
            {
                connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionAlreadyMember);
                return;
            }

            var nameError = GetNameError(name);
            if (nameError != ErrorMessageType.NoErrorMessage)
            {
                owner.SendErrorMessage(nameError);
                return;
            }

            // ----------------- Conditions, can change this...
            var team = teamManager.GetActiveTeamByUnit(owner.Id);
            if (team == null)// || !team.IsParty)
            {
                // We send the same error on number of party members when we don't have a party
                connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateMember);
                return;
            }

            // Check the number of members in the party that meet the requirements
            List<TeamMember> validMembers = [];
            List<TeamMember> teamMembers = [.. team.Members.ToList()];

            foreach (var m in teamMembers)
            {
                if (m?.Character == null)
                    continue;

                if (m.Character.Level < AppConfiguration.Instance.Expedition.Create.Level)
                {
                    connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateLevel);
                    return;
                }
                if (m.Character.Expedition != null)
                {
                    connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateMemberExpedition);
                    return;
                }
                if (m.Character.Faction == null || owner.Faction == null ||
                    GetAlliance(m.Character.Faction) != GetAlliance(owner.Faction))
                {
                    connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateFaction);
                    return;
                }
                validMembers.Add(m);
            }

            if (validMembers.Count < AppConfiguration.Instance.Expedition.Create.PartyMemberCount || validMembers.Count > MemberLimit)
            {
                connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateMember);
                return;
            }

            if (owner.Money < AppConfiguration.Instance.Expedition.Create.Cost)
            {
                connection.ActiveChar.SendErrorMessage(ErrorMessageType.ExpeditionCreateMoney);
                return;
            }

            if (!owner.SubtractMoney(SlotType.Inventory, AppConfiguration.Instance.Expedition.Create.Cost,
                    ItemTaskType.ExpeditionCreation))
                return;
            // -----------------

            var expedition = Create(name, owner);
            _expeditions.Add(expedition.Id, expedition);

            owner.Expedition = expedition;

            owner.SendPacket(
                new SCFactionCreatedPacket(expedition, owner.ObjId, [(owner.ObjId, owner.Id, owner.Name)])
            );

            worldManager.BroadcastPacketToServer(new SCFactionListPacket(expedition));
            owner.BroadcastPacket(
                new SCUnitExpeditionChangedPacket(owner.ObjId, owner.Id, "", owner.Name, 0, (uint)expedition.Id, false),
                true
            );

            chatManager.GetGuildChat(expedition).JoinChannel(owner);
            SendExpeditionInfo(owner);
            // owner.Save(); // Moved to SaveMananger

            foreach (var m in validMembers)
            {
                if (m.Character.Id == owner.Id)
                    continue;

                var invited = m.Character;
                var newMember = GetMemberFromCharacter(expedition, invited, false);

                invited.Expedition = expedition;
                expedition.Members.Add(newMember);

                invited.BroadcastPacket(
                    new SCUnitExpeditionChangedPacket(invited.ObjId, invited.Id, "", invited.Name, 0, (uint)expedition.Id, false),
                    true);
                SendExpeditionInfo(invited);
                expedition.OnCharacterLogin(invited);
                // invited.Save(); // Moved to SaveMananger
            }
            PersistExpedition(expedition);

            foreach (var member in validMembers)
                member.Character.Achievements.UpdateMaximum(CharRecordKind.EnrollGuild, 0, 0, 1);
        }
    }

    public void Invite(GameConnection connection, string invitedName)
    {
        lock (s_syncRoot)
        {
            RemoveExpiredInvitations();
            var inviter = connection.ActiveChar;
            if (!CurrentCharacter(inviter, connection))
                return;
            var expedition = inviter.Expedition;
            var inviterMember = expedition?.GetMember(inviter);
            if (inviterMember == null || expedition.isDisbanded ||
                !_expeditions.TryGetValue(expedition.Id, out var currentExpedition) ||
                !ReferenceEquals(expedition, currentExpedition) ||
                expedition.GetPolicyByRole(inviterMember.Role)?.Invite != true)
                return;

            var invited = worldManager.GetCharacter(invitedName);
            if (!CurrentCharacter(invited, invited?.Connection) || invited.Expedition != null ||
                CharacterBlocked.IsBlockedBy(invited, inviter.Id) || _pendingInvitations.ContainsKey(invited.Id))
                return;

            if (!CanJoinAlliance(expedition, inviter, invited))
            {
                inviter.SendErrorMessage(ErrorMessageType.ExpeditionBadFaction);
                return;
            }
            if (expedition.Members.Count >= MemberLimit)
            {
                inviter.SendErrorMessage(ErrorMessageType.ExpeditionMemberLimit);
                return;
            }

            _pendingInvitations.Add(invited.Id, new PendingInvitation(inviter, connection, invited, invited.Connection, expedition,
                InvitationTime().AddMinutes(1)));
            invited.SendPacket(new SCExpeditionInvitationPacket(inviter.Id, inviter.Name, (uint)expedition.Id, expedition.Name));
        }
    }

    private static bool CurrentCharacter(Character character, GameConnection connection) =>
        character != null && character.IsOnline && connection != null &&
        ReferenceEquals(character.Connection, connection) && ReferenceEquals(connection.ActiveChar, character);

    private void RemoveExpiredInvitations()
    {
        var now = InvitationTime();
        foreach (var (id, invitation) in _pendingInvitations.ToArray())
            if (invitation.Expires <= now || !CurrentCharacter(invitation.Invited, invitation.InvitedConnection) ||
                !CurrentCharacter(invitation.Inviter, invitation.InviterConnection))
                _pendingInvitations.Remove(id);
    }

    public void ReplyInvite(GameConnection connection, FactionsEnum id1, uint id2, bool reply)
    {
        lock (s_syncRoot)
        {
            RemoveExpiredInvitations();
            var invited = connection.ActiveChar;
            if (!CurrentCharacter(invited, connection) || !_pendingInvitations.TryGetValue(invited.Id, out var invitation) ||
                !ReferenceEquals(invitation.Invited, invited) || invitation.Expedition.Id != id1 || invitation.Inviter.Id != id2)
                return;
            _pendingInvitations.Remove(invited.Id);
            if (!reply)
                return;

            var inviter = invitation.Inviter;
            var expedition = invitation.Expedition;
            var inviterMember = expedition.GetMember(inviter);
            if (invited.Expedition != null || expedition.isDisbanded || !_expeditions.TryGetValue(id1, out var currentExpedition) ||
                !ReferenceEquals(expedition, currentExpedition) || !ReferenceEquals(inviter.Expedition, expedition) ||
                inviterMember == null || expedition.GetPolicyByRole(inviterMember.Role)?.Invite != true ||
                CharacterBlocked.IsBlockedBy(invited, inviter.Id) || expedition.GetMember(invited) != null)
                return;

            if (!CanJoinAlliance(expedition, inviter, invited))
            {
                invited.SendErrorMessage(ErrorMessageType.ExpeditionBadFaction);
                return;
            }
            if (expedition.Members.Count >= MemberLimit)
            {
                invited.SendErrorMessage(ErrorMessageType.ExpeditionCannotJoinMemberFull);
                return;
            }

            var newMember = GetMemberFromCharacter(expedition, invited, false);
            expedition.Members.Add(newMember);
            try
            {
                PersistExpedition(expedition);
            }
            catch
            {
                expedition.Members.Remove(newMember);
                throw;
            }
            invited.Expedition = expedition;
            invited.BroadcastPacket(
                new SCUnitExpeditionChangedPacket(invited.ObjId, invited.Id, "", invited.Name, 0, (uint)expedition.Id, false), true);
            SendExpeditionInfo(invited);
            expedition.OnCharacterLogin(invited);
        }
    }

    public void ChangeExpeditionRolePolicy(GameConnection connection, ExpeditionRolePolicy policy)
    {
        lock (s_syncRoot)
        {
            if (!_expeditions.TryGetValue(policy.ExpeditionId, out var expedition) || expedition.isDisbanded ||
                !ReferenceEquals(connection.ActiveChar.Expedition, expedition) || !expedition.IsOwner(connection.ActiveChar))
                return;

            var currentPolicy = expedition.GetPolicyByRole(policy.Role);
            if (currentPolicy == null || string.IsNullOrWhiteSpace(policy.Name) || policy.Name.Length > 128)
                return;

            var previous = currentPolicy.Clone();
            currentPolicy.CopyFrom(policy);
            try
            {
                PersistExpedition(expedition);
            }
            catch
            {
                currentPolicy.CopyFrom(previous);
                throw;
            }
            expedition.SendPacket(new SCExpeditionRolePolicyChangedPacket(currentPolicy, true));
        }
    }

    /// <summary>
    /// Removes a character from their Guild
    /// </summary>
    /// <param name="character"></param>
    public void Leave(Character character)
    {
        lock (s_syncRoot)
        {
            var expedition = character.Expedition;
            if (expedition == null || expedition.GetMember(character) == null || expedition.isDisbanded) return;
            if (expedition.OwnerId == character.Id)
            {
                character.SendErrorMessage(ErrorMessageType.ExpeditionOwnerCannotLeave);
                return;
            }

            RemoveMemberAndSave(expedition, expedition.GetMember(character));
            chatManager.GetGuildChat(expedition).LeaveChannel(character);
            var changedPacket = new SCUnitExpeditionChangedPacket(
                character.ObjId,
                character.Id,
                "",
                character.Name,
                (uint)expedition.Id,
                0,
                false
            );
            character.Expedition = null;
            character.BroadcastPacket(changedPacket, true);
            expedition.SendPacket(changedPacket);
        }
    }

    public void Kick(GameConnection connection, uint kickedId)
    {
        lock (s_syncRoot)
        {
            var character = connection.ActiveChar;
            var expedition = character.Expedition;

            var characterMember = expedition?.GetMember(character);
            if (characterMember == null || expedition.isDisbanded || expedition.GetPolicyByRole(characterMember.Role)?.Expel != true)
                return;

            var kicked = expedition.GetMember(kickedId);
            if (!expedition.CanManageMember(characterMember, kicked))
                return;

            RemoveMemberAndSave(expedition, kicked);

            var kickedChar = worldManager.GetCharacterById(kickedId);

            var changedPacket = new SCUnitExpeditionChangedPacket(kickedChar?.ObjId ?? 0,
                kicked.CharacterId, character.Name, kicked.Name, (uint)expedition.Id, 0, true);

            if (kickedChar is not null)
            {
                kickedChar.Expedition = null;
                chatManager.GetGuildChat(expedition).LeaveChannel(kickedChar);
                kickedChar.BroadcastPacket(changedPacket, true);
            }
            expedition.SendPacket(changedPacket);
        }
    }

    public void ChangeMemberRole(GameConnection connection, byte newRole, uint changedId)
    {
        lock (s_syncRoot)
        {
            var character = connection.ActiveChar;
            var expedition = character.Expedition;

            var changerMember = expedition?.GetMember(character);
            if (changerMember == null ||
                expedition.isDisbanded || changerMember.Role <= newRole ||
                expedition.GetPolicyByRole(newRole) == null ||
                expedition.GetPolicyByRole(changerMember.Role)?.Promote != true)
                return;

            var changedMember = expedition.GetMember(changedId);
            if (!expedition.CanManageMember(changerMember, changedMember))
                return;

            var previousRole = changedMember.Role;
            changedMember.Role = newRole;
            try
            {
                PersistExpedition(expedition);
            }
            catch
            {
                changedMember.Role = previousRole;
                throw;
            }
            expedition.SendPacket(
                new SCExpeditionRoleChangedPacket(changedMember.CharacterId, changedMember.Role, changedMember.Name)
            );
        }
    }

    public void ChangeOwner(GameConnection connection, uint newOwnerId)
    {
        lock (s_syncRoot)
        {
            var owner = connection.ActiveChar;
            var expedition = owner.Expedition;

            var ownerMember = expedition?.GetMember(owner);
            if (ownerMember == null || !expedition.IsOwner(owner))
                return;

            var newOwnerMember = expedition.GetMember(newOwnerId);
            if (newOwnerMember == null || newOwnerMember.CharacterId == owner.Id) return;

            var previousRole = newOwnerMember.Role;
            var previousOwnerName = expedition.OwnerName;
            newOwnerMember.Role = 255;
            ownerMember.Role = 0;

            expedition.OwnerId = newOwnerId;
            expedition.OwnerName = newOwnerMember.Name;
            try
            {
                PersistExpedition(expedition);
            }
            catch
            {
                newOwnerMember.Role = previousRole;
                ownerMember.Role = 255;
                expedition.OwnerId = owner.Id;
                expedition.OwnerName = previousOwnerName;
                throw;
            }

            expedition.SendPacket(
                new SCExpeditionOwnerChangedPacket(
                    ownerMember.CharacterId,
                    newOwnerMember.CharacterId,
                    newOwnerMember.Name
                )
            );
            expedition.SendPacket(
                new SCExpeditionRoleChangedPacket(ownerMember.CharacterId, ownerMember.Role, ownerMember.Name)
            );
            expedition.SendPacket(
                new SCExpeditionRoleChangedPacket(newOwnerMember.CharacterId, newOwnerMember.Role, newOwnerMember.Name)
            );
        }
    }

    private void RemoveMemberAndSave(Expedition expedition, ExpeditionMember member)
    {
        var index = expedition.Members.IndexOf(member);
        expedition.RemoveMember(member);
        try
        {
            PersistExpedition(expedition);
        }
        catch
        {
            expedition.RestoreMember(member, index);
            throw;
        }
    }

    public bool Disband(Character owner)
    {
        lock (s_syncRoot)
        {
            var guild = owner.Expedition;
            if (guild == null || guild.isDisbanded || !_expeditions.TryGetValue(guild.Id, out var currentGuild) ||
                !ReferenceEquals(guild, currentGuild))
            {
                owner.SendErrorMessage(ErrorMessageType.OnlyExpeditionMember);
                return false;
            }
            if (!guild.IsOwner(owner))
            {
                owner.SendErrorMessage(ErrorMessageType.OnlyExpeditionOwner);
                return false;
            }

            guild.isDisbanded = true;
            try
            {
                PersistExpedition(guild);
            }
            catch
            {
                guild.isDisbanded = false;
                throw;
            }

            foreach (var member in guild.Members.ToArray())
            {
                var character = worldManager.GetCharacterById(member.CharacterId);
                if (character != null)
                {
                    character.Expedition = null;
                    chatManager.GetGuildChat(guild).LeaveChannel(character);
                    character.BroadcastPacket(new SCUnitExpeditionChangedPacket(character.ObjId, character.Id,
                        "", character.Name, (uint)guild.Id, 0, false), true);
                }
            }
            guild.Members.Clear();
            guild.OwnerId = 0;
            guild.OwnerName = "";
            _expeditions.Remove(guild.Id);
            foreach (var (id, invitation) in _pendingInvitations.ToArray())
                if (ReferenceEquals(invitation.Expedition, guild))
                    _pendingInvitations.Remove(id);
            worldManager.BroadcastPacketToServer(new SCExpeditionDismissedPacket((uint)guild.Id, true));
            expeditionIdManager.ReleaseId((uint)guild.Id);
            return true;
        }
    }

    public bool Rename(Character owner, FactionsEnum id, string name, bool isExpedition)
    {
        lock (s_syncRoot)
        {
            if (!isExpedition || !_expeditions.TryGetValue(id, out var expedition) ||
                !ReferenceEquals(owner.Expedition, expedition) || !expedition.IsOwner(owner))
                return false;
            var nameError = GetNameError(name, expedition);
            if (nameError != ErrorMessageType.NoErrorMessage)
            {
                owner.SendErrorMessage(nameError);
                return false;
            }

            var previousName = expedition.Name;
            expedition.Name = name;
            try
            {
                PersistExpedition(expedition);
            }
            catch
            {
                expedition.Name = previousName;
                throw;
            }
            chatManager.GetGuildChat(expedition).InternalName = name;
            worldManager.BroadcastPacketToServer(new SCFactionRenamedPacket((uint)expedition.Id, name, false));
            return true;
        }
    }

    private ErrorMessageType GetNameError(string name, Expedition renamed = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
            return ErrorMessageType.ExpeditionNameLength;
        var match = _nameRegex.Match(name);
        if (!match.Success || match.Index != 0 || match.Length != name.Length)
            return ErrorMessageType.ExpeditionNameCharacter;
        if (_expeditions.Values.Any(expedition => !ReferenceEquals(expedition, renamed) &&
                string.Equals(name, expedition.Name, StringComparison.OrdinalIgnoreCase)))
            return ErrorMessageType.ExpeditionNameExist;
        return ErrorMessageType.NoErrorMessage;
    }

    private static bool CanJoinAlliance(Expedition expedition, Character inviter, Character invited)
    {
        if (inviter.Faction == null || invited.Faction == null)
            return false;
        var alliance = expedition.MotherId;
        if (alliance != GetAlliance(inviter.Faction))
        {
            var guildFaction = FactionManager.Instance.GetFaction(expedition.MotherId);
            if (guildFaction != null)
                alliance = GetAlliance(guildFaction);
        }
        return alliance == GetAlliance(inviter.Faction) && alliance == GetAlliance(invited.Faction);
    }

    private static FactionsEnum GetAlliance(SystemFaction faction) =>
        faction.MotherId == FactionsEnum.Invalid ? faction.Id : faction.MotherId;

    public static void SendExpeditionInfo(Character character)
    {
        var members = character.Expedition.Members;
        var total = (uint)members.Count;
        var id = character.Expedition.Id;

        character.SendPacket(new SCExpeditionRolePolicyListPacket(character.Expedition.Policies));

        for (var i = 0; i < members.Count; i += 20)
        {
            var block = members.Skip(i).Take(20).ToList();
            character.SendPacket(new SCExpeditionMemberListPacket(total, (uint)id, block));
        }
    }

    public static void Save(Expedition expedition)
    {
        lock (s_syncRoot)
        {
            var save = SaveManager.Instance;
            save.ThrowIfConsistencyFailed();
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            var commitAttempted = false;
            try
            {
                expedition.Save(connection, transaction);
                commitAttempted = true;
                save.CommitTransaction(transaction);
                expedition.OnSaved();
            }
            catch (Exception exception) when (commitAttempted)
            {
                save.FailForConsistency(exception);
                throw;
            }
        }
    }

    public static ExpeditionMember GetMemberFromCharacter(Expedition expedition, Character character, bool owner)
    {
        var member = new ExpeditionMember
        {
            IsOnline = true,
            Name = character.Name,
            Level = character.Level,
            Role = (byte)(owner ? 255 : 0),
            Memo = "",
            Position = new Vector3(character.Transform.World.Position.X, character.Transform.World.Position.Y, character.Transform.World.Position.Z),
            ZoneId = character.Transform.ZoneId,
            Abilities = [(byte)character.Ability1, (byte)character.Ability2, (byte)character.Ability3],
            ExpeditionId = expedition.Id,
            CharacterId = character.Id,
            LastWorldLeaveTime = DateTime.UtcNow
        };

        return member;
    }

    public void SendExpeditions(Character character)
    {
        lock (s_syncRoot)
        {
            if (_expeditions.Values.Count > 0)
            {
                var expeditions = _expeditions.Values.ToArray();
                for (var i = 0; i < expeditions.Length; i += 20)
                {
                    var temp = new SystemFaction[expeditions.Length - i <= 20 ? expeditions.Length - i : 20];
                    Array.Copy(expeditions, i, temp, 0, temp.Length);
                    character.SendPacket(new SCFactionListPacket(temp));
                }
            }

            character.SendPacket(new SCExpeditionRolePolicyListPacket([]));
        }
    }

    public FactionsEnum GetExpeditionOfCharacter(uint characterId)
    {
        lock (s_syncRoot)
        {
            return (from guild in _expeditions.Values from member in guild.Members where member.CharacterId == characterId select guild.Id).FirstOrDefault();
        }
    }
}
