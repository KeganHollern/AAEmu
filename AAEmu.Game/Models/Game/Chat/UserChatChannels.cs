using System.Text;

using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Names;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Chat;

/// <summary>Session-only user channels. A private lock serializes registry and faction assignments.</summary>
internal sealed class UserChatChannels
{
    internal const int MaximumMemberships = 5;
    private readonly object _syncRoot = new();
    private readonly Dictionary<ulong, Channel> _channels = [];
    private readonly Dictionary<FactionsEnum, Dictionary<string, Channel>> _names = [];
    private readonly Dictionary<Character, HashSet<ulong>> _memberships = new(ReferenceEqualityComparer.Instance);
    private ulong _nextId;

    private sealed class Channel(ulong key, FactionsEnum faction, string name, string password)
    {
        internal ulong Key { get; } = key;
        internal FactionsEnum Faction { get; } = faction;
        internal string Name { get; } = name;
        internal string Password { get; } = password;
        internal Dictionary<Character, GameConnection> Members { get; } = new(ReferenceEqualityComparer.Instance);
    }

    internal ulong Join(GameConnection connection, string name, string password, bool create)
    {
        lock (_syncRoot)
        {
            var character = connection?.ActiveChar;
            if (!Current(character, connection))
                return 0;
            RemoveInvalidMemberships(character);
            if (!ValidName(name) || !ValidPassword(password))
            {
                character.SendMessage("Channel names need 1 to 48 UTF-8 bytes. Passwords can use up to 6 UTF-8 bytes.");
                return 0;
            }
            var faction = Allegiance(character);
            if (faction == FactionsEnum.Invalid)
            {
                character.SendErrorMessage(ErrorMessageType.ChatNoChannel);
                return 0;
            }
            _names.TryGetValue(faction, out var names);
            var channel = names?.GetValueOrDefault(name);
            if (channel != null)
            {
                Prune(channel);
                channel = names.GetValueOrDefault(name);
            }
            if (create && channel != null)
                return Reject(character, ErrorMessageType.ChatChannelAlreadyExists);
            if (!create && channel == null)
                return Reject(character, ErrorMessageType.ChatNoChannel);
            if (channel?.Members.ContainsKey(character) == true)
                return Reject(character, ErrorMessageType.ChatAlreadyJoinedChannel);
            if (channel != null && channel.Password.Length > 0 && password.Length == 0)
                return Reject(character, ErrorMessageType.ChatPrivateChannel);
            if (channel != null && !string.Equals(channel.Password, password, StringComparison.Ordinal))
                return Reject(character, ErrorMessageType.ChatWrongPassword);
            if (_memberships.TryGetValue(character, out var memberships) && memberships.Count >= MaximumMemberships)
            {
                character.SendMessage("You can join up to 5 user channels. Leave a channel before you join another.");
                return 0;
            }
            if (channel == null)
            {
                // The native client compares all 64 bits. Never recycle a key in this session registry.
                if (_nextId == ulong.MaxValue >> 16)
                    return Reject(character, ErrorMessageType.InternalError);
                var key = (++_nextId << 16) | (ushort)ChatType.User;
                channel = new Channel(key, faction, name, password);
                _channels.Add(key, channel);
                if (!_names.TryGetValue(faction, out names))
                {
                    names = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
                    _names.Add(faction, names);
                }
                names.Add(name, channel);
            }
            if (memberships == null)
            {
                memberships = [];
                _memberships.Add(character, memberships);
            }
            memberships.Add(channel.Key);
            channel.Members.Add(character, connection);
            connection.SendPacket(new SCJoinedChatChannelPacket(channel.Key, channel.Name));
            return channel.Key;
        }
    }

    internal bool Leave(GameConnection connection, ulong key)
    {
        lock (_syncRoot)
        {
            var character = connection?.ActiveChar;
            if (!Current(character, connection))
                return false;
            if (!_channels.TryGetValue(key, out var channel) ||
                !channel.Members.TryGetValue(character, out var memberConnection) || !ReferenceEquals(memberConnection, connection))
            {
                character.SendErrorMessage(ErrorMessageType.ChatNotJoinedChannel);
                return false;
            }
            Remove(channel, character, true);
            return true;
        }
    }

    internal bool Send(GameConnection connection, ulong key, string message, int ability, byte language)
    {
        lock (_syncRoot)
        {
            var character = connection?.ActiveChar;
            if (!Current(character, connection))
                return false;
            if (!_channels.TryGetValue(key, out var channel))
            {
                character.SendErrorMessage(ErrorMessageType.ChatNotJoinedChannel);
                return false;
            }
            Prune(channel);
            if (!channel.Members.TryGetValue(character, out var memberConnection) || !ReferenceEquals(memberConnection, connection))
            {
                character.SendErrorMessage(ErrorMessageType.ChatNotJoinedChannel);
                return false;
            }
            foreach (var recipient in channel.Members.Values)
                recipient.SendPacket(new SCChatMessagePacket(key, character, message, ability, language));
            return true;
        }
    }

    internal void LeaveAll(Character character)
    {
        lock (_syncRoot)
        {
            if (character == null || !_memberships.TryGetValue(character, out var keys))
                return;
            foreach (var key in keys.ToArray())
                Remove(_channels[key], character, false);
        }
    }

    internal void ChangeFaction(Character character, SystemFaction faction)
    {
        // Buffs can initiate a faction change while holding their own lock.
        // Keep this boundary limited to assignment and channel state, without buff callbacks.
        lock (_syncRoot)
        {
            character.Faction = faction;
            RemoveInvalidMemberships(character);
        }
    }

    private void RemoveInvalidMemberships(Character character)
    {
        lock (_syncRoot)
        {
            if (character == null || !_memberships.TryGetValue(character, out var keys))
                return;
            foreach (var key in keys.ToArray())
            {
                var channel = _channels[key];
                if (!Current(character, channel.Members[character]) || Allegiance(character) != channel.Faction)
                    Remove(channel, character, true);
            }
        }
    }

    private void Prune(Channel channel)
    {
        foreach (var (character, connection) in channel.Members.ToArray())
            if (!Current(character, connection) || Allegiance(character) != channel.Faction)
                Remove(channel, character, true);
    }

    private void Remove(Channel channel, Character character, bool notify)
    {
        if (!channel.Members.Remove(character, out var connection))
            return;
        var memberships = _memberships[character];
        memberships.Remove(channel.Key);
        if (memberships.Count == 0)
            _memberships.Remove(character);
        if (channel.Members.Count == 0)
        {
            _channels.Remove(channel.Key);
            var names = _names[channel.Faction];
            names.Remove(channel.Name);
            if (names.Count == 0)
                _names.Remove(channel.Faction);
        }
        if (notify && !connection.IsClosed && ReferenceEquals(character.Connection, connection))
            connection.SendPacket(new SCLeavedChatChannelPacket(channel.Key));
    }

    private static ulong Reject(Character character, ErrorMessageType error)
    {
        character.SendErrorMessage(error);
        return 0;
    }

    private static bool Current(Character character, GameConnection connection)
    {
        return character != null && character.Id != 0 && character.AccountId != 0 && character.IsOnline && connection != null && !connection.IsClosed &&
            character.AccountId == connection.AccountId &&
            ReferenceEquals(character.Connection, connection) && ReferenceEquals(connection.ActiveChar, character);
    }

    private static FactionsEnum Allegiance(Character character)
    {
        var faction = character.Faction;
        if (faction == null || faction.Id == FactionsEnum.Invalid)
            return FactionsEnum.Invalid;
        return faction.MotherId != FactionsEnum.Invalid ? faction.MotherId : faction.Id;
    }

    internal static bool ValidName(string name)
    {
        return NameRules.IsWellFormed(name) && Encoding.UTF8.GetByteCount(name) <= 48 &&
            !char.IsWhiteSpace(name[0]) && !char.IsWhiteSpace(name[^1]) && !name.Contains('|') && !NameRules.IsReserved(name);
    }

    internal static bool ValidPassword(string password)
    {
        return password != null && (password.Length == 0 || NameRules.IsWellFormed(password)) &&
            Encoding.UTF8.GetByteCount(password) <= 6;
    }
}
