using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class FamilyManager(IWorldManager worldManager, IChatManager chatManager, IFamilyIdManager familyIdManager) : Singleton<FamilyManager>, IFamilyManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const int MaximumTitleCharacters = 45; // family_members.title varchar(45)
    private const int MaximumTitleBytes = 104; // Native FamilyMember title buffer, excluding the terminator.

    private Dictionary<uint, Family> _families = [];
    private Dictionary<uint, FamilyMember> _familyMembers = [];
    private readonly object _syncRoot = SaveManager.PersistenceSyncRoot;
    private readonly Dictionary<uint, PendingInvitation> _pendingInvitations = [];

    internal Func<DateTime> InvitationTime { get; set; } = () => DateTime.UtcNow;
    internal Action<Family> PersistFamily { get; set; } = SaveFamily;

    private sealed record PendingInvitation(Character Inviter, GameConnection InviterConnection,
        Character Invited, GameConnection InvitedConnection, Family Family, string Title, DateTime Expires);

    /// <summary>
    /// Load family data
    /// </summary>
    public void Load()
    {
        _families = [];
        _familyMembers = [];

        Logger.Info("Loading families...");
        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT family FROM characters";
                command.Prepare();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var familyId = reader.GetUInt32("family");
                        if (familyId == 0)
                            continue;

                        var family = new Family { Id = familyId };
                        _families.Add(family.Id, family);

                        using (var connection2 = MySQL.CreateConnection())
                        {
                            family.Load(connection2); // TODO : Maybe find a prettier way
                        }

                        foreach (var member in family.Members)
                            _familyMembers.Add(member.Id, member);
                    }
                }
            }
        }

        Logger.Info($"Loaded {_families.Count} families");
    }

    /// <summary>
    /// Force save all families
    /// </summary>
    public void SaveAllFamilies()
    {
        lock (_syncRoot)
        {
            var save = SaveManager.Instance;
            save.ThrowIfConsistencyFailed();
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            var commitAttempted = false;
            try
            {
                foreach (var family in _families.Values)
                    family.Save(connection, transaction);
                commitAttempted = true;
                save.CommitTransaction(transaction);
                foreach (var family in _families.Values)
                    family.AcceptSave();
            }
            catch (Exception exception) when (commitAttempted)
            {
                save.FailForConsistency(exception);
                throw;
            }
        }
    }

    /// <summary>
    /// Save family data
    /// </summary>
    /// <param name="family"></param>
    public static void SaveFamily(Family family)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var save = SaveManager.Instance;
            save.ThrowIfConsistencyFailed();
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            var commitAttempted = false;
            try
            {
                family.Save(connection, transaction);
                commitAttempted = true;
                save.CommitTransaction(transaction);
                family.AcceptSave();
            }
            catch (Exception exception) when (commitAttempted)
            {
                save.FailForConsistency(exception);
                throw;
            }
        }
    }

    /// <summary>
    /// Sends invite request
    /// </summary>
    /// <param name="inviter"></param>
    /// <param name="invitedCharacterName"></param>
    /// <param name="title"></param>
    public void InviteToFamily(Character inviter, string invitedCharacterName, string title)
    {
        lock (_syncRoot)
        {
            RemoveExpiredInvitations();
            if (!CurrentCharacter(inviter, inviter?.Connection))
                return;
            if (!IsValidTitle(title))
            {
                inviter.SendErrorMessage(ErrorMessageType.FamilyTitleBad);
                return;
            }

            Family family = null;
            if (inviter.Family != 0 &&
                (!_families.TryGetValue(inviter.Family, out family) || family.GetMember(inviter) == null))
                return;
            if (family != null && family.GetMember(inviter)?.Role != 1)
            {
                inviter.SendErrorMessage(ErrorMessageType.FamilyNotOwner);
                return;
            }
            if (family?.Members.Count >= Family.MaximumMembers)
            {
                inviter.SendErrorMessage(ErrorMessageType.FamilyMaximum);
                return;
            }

            var invited = worldManager.GetCharacter(invitedCharacterName);
            if (!CurrentCharacter(invited, invited?.Connection) || invited.Id == inviter.Id || invited.Family != 0 ||
                _familyMembers.ContainsKey(invited.Id) || CharacterBlocked.IsBlockedBy(invited, inviter.Id) ||
                _pendingInvitations.ContainsKey(invited.Id))
                return;

            _pendingInvitations.Add(invited.Id, new PendingInvitation(inviter, inviter.Connection,
                invited, invited.Connection, family, title ?? "", InvitationTime().AddMinutes(1)));
            invited.SendPacket(new SCFamilyInvitationPacket(inviter.Id, inviter.Name, 1, title ?? ""));
        }
    }

    private static bool IsValidTitle(string title) => title == null ||
        title.Length <= MaximumTitleCharacters && Encoding.UTF8.GetByteCount(title) <= MaximumTitleBytes;

    private static bool CurrentCharacter(Character character, GameConnection connection) =>
        character != null && character.IsOnline && connection != null &&
        ReferenceEquals(character.Connection, connection) && ReferenceEquals(connection.ActiveChar, character);

    private void RemoveExpiredInvitations()
    {
        var now = InvitationTime();
        foreach (var (id, invitation) in _pendingInvitations.ToArray())
            if (invitation.Expires <= now || !CurrentCharacter(invitation.Inviter, invitation.InviterConnection) ||
                !CurrentCharacter(invitation.Invited, invitation.InvitedConnection))
                _pendingInvitations.Remove(id);
    }

    /// <summary>
    /// Consumes the invitation chosen by the server. The reply title is not authoritative.
    /// </summary>
    public void ReplyToInvite(uint invitorId, Character invitedChar, bool join, string title)
    {
        lock (_syncRoot)
        {
            RemoveExpiredInvitations();
            if (invitedChar == null || !_pendingInvitations.TryGetValue(invitedChar.Id, out var invitation) ||
                !ReferenceEquals(invitation.Invited, invitedChar) || invitation.Inviter.Id != invitorId)
                return;
            _pendingInvitations.Remove(invitedChar.Id);
            if (!join || invitedChar.Family != 0 || _familyMembers.ContainsKey(invitedChar.Id) ||
                CharacterBlocked.IsBlockedBy(invitedChar, invitorId))
                return;

            var invitor = invitation.Inviter;
            var family = invitation.Family;
            if (family == null)
            {
                if (invitor.Family == 0 && !_familyMembers.ContainsKey(invitor.Id))
                    CreateFamily(invitor, invitedChar, invitation.Title);
                return;
            }

            if (invitor.Family != family.Id || !_families.TryGetValue(family.Id, out var currentFamily) ||
                !ReferenceEquals(currentFamily, family) || family.GetMember(invitor)?.Role != 1)
                return;
            if (family.Members.Count >= Family.MaximumMembers)
            {
                invitedChar.SendErrorMessage(ErrorMessageType.FamilyMaximum);
                return;
            }

            var stagedFamily = CopyFamily(family);
            stagedFamily.AddMember(GetMemberForCharacter(invitedChar, 0, invitation.Title));
            if (!TrySaveFamily(stagedFamily))
                return;

            AddFamilyMember(family, invitedChar, invitation.Title);
            family.SendPacket(new SCFamilyMemberAddedPacket(family, family.Members.Count - 1));
            invitedChar.Achievements.Increment(CharRecordKind.EnrollFamily, 0, 0);
        }
    }

    private void CreateFamily(Character invitor, Character invitedChar, string invitedCharTitle)
    {
        var family = new Family { Id = familyIdManager.GetNextId() };
        family.AddMember(GetMemberForCharacter(invitor, 1, ""));
        family.AddMember(GetMemberForCharacter(invitedChar, 0, invitedCharTitle));
        if (!TrySaveFamily(family))
        {
            familyIdManager.ReleaseId(family.Id);
            return;
        }

        _families.Add(family.Id, family);
        foreach (var member in family.Members)
        {
            _familyMembers.Add(member.Id, member);
            member.Character.Family = family.Id;
            chatManager.GetFamilyChat(family.Id)?.JoinChannel(member.Character);
        }
        family.SendPacket(new SCFamilyCreatedPacket(family));
        invitor.Achievements.Increment(CharRecordKind.EnrollFamily, 0, 0);
        invitedChar.Achievements.Increment(CharRecordKind.EnrollFamily, 0, 0);
    }

    private bool TrySaveFamily(Family family)
    {
        try
        {
            PersistFamily(family);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to save family {0}", family.Id);
            return false;
        }
    }

    private static Family CopyFamily(Family family)
    {
        var copy = new Family { Id = family.Id };
        foreach (var member in family.Members)
            copy.AddMember(new FamilyMember
            {
                Character = member.Character, Id = member.Id, Name = member.Name, Role = member.Role, Title = member.Title
            });
        return copy;
    }

    /// <summary>
    /// Adds a character to a family.
    /// </summary>
    /// <param name="family">The family to add the character to.</param>
    /// <param name="character">The character to add to the family.</param>
    /// <param name="title">The title given to the character by the family owner. Only used if the character is not also the owner.</param>
    /// <remarks>
    /// If the family is empty, the first call to this method will add the character as the owner of the family.
    /// The character is joined to the family chat channel.
    /// This method does not send any packets, and no checks are made as to whether the character is already in a family.
    /// </remarks>
    private void AddFamilyMember(Family family, Character character, string title = null)
    {
        var isOwner = family.Members.Count == 0;
        var ownerFlag = (byte)(isOwner ? 1 : 0);
        if (isOwner || title == null) title = "";

        var member = GetMemberForCharacter(character, ownerFlag, title);
        family.AddMember(member);
        _familyMembers.Add(member.Id, member);
        character.Family = family.Id;

        chatManager.GetFamilyChat(family.Id)?.JoinChannel(character);
    }

    /// <summary>
    /// Called by a character when logging in. Sends the character a family description packet. Sends every other family member an Online update packet.
    /// </summary>
    /// <param name="character"></param>
    public void OnCharacterLogin(Character character)
    {
        lock (_syncRoot)
        {
            var family = _families.GetValueOrDefault(character.Family);
            var member = family?.GetMember(character);
            if (family == null || member == null)
            {
                character.Family = 0;
                return;
            }

            member.Character = character;
            chatManager.GetFamilyChat(family.Id)?.JoinChannel(character);
            character.SendPacket(new SCFamilyDescPacket(family));
            family.SendPacket(new SCFamilyMemberOnlinePacket(family.Id, member.Id, true));
            character.Achievements.UpdateMaximum(CharRecordKind.EnrollFamily, 0, 0, 1);
        }
    }

    /// <summary>
    /// Called when a player logs out. Sends an update to every family member to mark him as offline.
    /// </summary>
    /// <param name="character"></param>
    public void OnCharacterLogout(Character character)
    {
        lock (_syncRoot)
        {
            foreach (var (id, invitation) in _pendingInvitations.ToArray())
                if (invitation.Inviter.Id == character.Id || invitation.Invited.Id == character.Id)
                    _pendingInvitations.Remove(id);
            if (!_families.TryGetValue(character.Family, out var family))
                return;
            var member = family.GetMember(character);
            if (member == null || !ReferenceEquals(member.Character, character))
                return;
            member.Character = null;
            chatManager.GetFamilyChat(family.Id)?.LeaveChannel(character);
            family.SendPacket(new SCFamilyMemberOnlinePacket(family.Id, character.Id, false), character.Id);
        }
    }

    /// <summary>
    /// Removes a member and persists the resulting family before notifying its members.
    /// </summary>
    public void LeaveFamily(Character character)
    {
        lock (_syncRoot)
        {
            if (character != null && _families.TryGetValue(character.Family, out var family) &&
                family.GetMember(character) is { Role: 0 } member)
                RemoveFamilyMember(family, member, false);
        }
    }

    /// <summary>
    /// Character deletion must not leave a family with a missing steward.
    /// </summary>
    public void RemoveDeletedCharacter(Character character)
    {
        lock (_syncRoot)
        {
            if (character != null && _families.TryGetValue(character.Family, out var family) &&
                family.GetMember(character) is { } member)
                RemoveFamilyMember(family, member, false, member.Role == 1);
        }
    }

    private void RemoveFamilyMember(Family family, FamilyMember member, bool kicked, bool forceDisband = false)
    {
        var disband = forceDisband || family.Members.Count <= 2;
        var removed = disband ? family.Members.ToArray() : [member];
        var stagedFamily = CopyFamily(family);
        foreach (var target in removed)
            stagedFamily.RemoveMember(stagedFamily.GetMember(target.Id));
        if (!TrySaveFamily(stagedFamily))
            return;

        foreach (var target in removed)
        {
            family.RemoveMember(target);
            _familyMembers.Remove(target.Id);
            if (target.Character != null)
            {
                target.Character.Family = 0;
                chatManager.GetFamilyChat(family.Id)?.LeaveChannel(target.Character);
                target.Character.SendPacket(new SCFamilyRemovedPacket(family.Id));
            }
        }
        family.AcceptSave();
        if (disband)
        {
            _families.Remove(family.Id);
            familyIdManager.ReleaseId(family.Id);
        }
        else
            family.SendPacket(new SCFamilyMemberRemovedPacket(family.Id, kicked, member.Id));
    }

    /// <summary>
    /// Removes only another member of the steward's family, including an offline member.
    /// </summary>
    public void KickMember(Character kicker, uint kickedId)
    {
        lock (_syncRoot)
        {
            if (!TryGetOwnedFamilyMember(kicker, kickedId, out var family, out var member) || member.Id == kicker.Id)
                return;
            RemoveFamilyMember(family, member, true);
        }
    }

    private bool TryGetOwnedFamilyMember(Character owner, uint memberId, out Family family, out FamilyMember member)
    {
        family = null;
        member = null;
        if (owner == null || !_families.TryGetValue(owner.Family, out family) || family.GetMember(owner)?.Role != 1)
            return false;
        member = family.GetMember(memberId);
        return member != null;
    }

    /// <summary>
    /// Changes the title of a member
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="memberId"></param>
    /// <param name="newTitle"></param>
    public void ChangeTitle(Character owner, uint memberId, string newTitle)
    {
        lock (_syncRoot)
        {
            if (!TryGetOwnedFamilyMember(owner, memberId, out var family, out var member))
                return;
            if (!IsValidTitle(newTitle))
            {
                owner.SendErrorMessage(ErrorMessageType.FamilyTitleBad);
                return;
            }
            var stagedFamily = CopyFamily(family);
            stagedFamily.GetMember(memberId).Title = newTitle ?? "";
            if (!TrySaveFamily(stagedFamily))
                return;
            member.Title = newTitle ?? "";
            family.SendPacket(new SCFamilyTitleChangedPacket(family.Id, memberId, member.Title));
        }
    }

    /// <summary>
    /// Changes the Steward of a Family
    /// </summary>
    /// <param name="previousOwner"></param>
    /// <param name="memberId"></param>
    public void ChangeOwner(Character previousOwner, uint memberId)
    {
        lock (_syncRoot)
        {
            if (!TryGetOwnedFamilyMember(previousOwner, memberId, out var family, out var member) ||
                member.Id == previousOwner.Id)
                return;
            var stagedFamily = CopyFamily(family);
            stagedFamily.GetMember(memberId).Role = 1;
            stagedFamily.GetMember(previousOwner).Role = 0;
            if (!TrySaveFamily(stagedFamily))
                return;
            member.Role = 1;
            family.GetMember(previousOwner).Role = 0;
            family.SendPacket(new SCFamilyOwnerChangedPacket(family.Id, memberId));
            family.SendPacket(new SCFamilyDescPacket(family));
        }
    }

    /// <summary>
    /// Get Family by Id
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public Family GetFamily(uint id)
    {
        lock (_syncRoot)
            return _families.GetValueOrDefault(id);
    }

    /// <summary>
    /// Creates a Member object from a Character
    /// </summary>
    /// <param name="character"></param>
    /// <param name="owner">Is Owner Flag (role)</param>
    /// <param name="title"></param>
    /// <returns></returns>
    private static FamilyMember GetMemberForCharacter(Character character, byte owner, string title)
    {
        return new FamilyMember
        {
            Character = character,
            Id = character.Id,
            Name = character.Name,
            Role = owner,
            Title = title
        };
    }

    /// <summary>
    /// Gets FamilyId of an offline or online character
    /// </summary>
    /// <param name="characterId"></param>
    /// <returns></returns>
    public uint GetFamilyOfCharacter(uint characterId)
    {
        lock (_syncRoot)
        {
            foreach (var family in _families.Values)
                if (family.GetMember(characterId) != null)
                    return family.Id;
            return 0;
        }
    }
}
