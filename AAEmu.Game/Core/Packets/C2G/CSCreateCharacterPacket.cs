using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCreateCharacterPacket() : GamePacket(CSOffsets.CSCreateCharacterPacket, 1)
{
    internal sealed record Request(string Name, Race Race, Gender Gender, UnitCustomModelParams CustomModel, AbilityType Ability);

    internal static bool TryReadRequest(PacketStream stream, out Request request)
    {
        request = null;
        try
        {
            var nameLength = stream.ReadInt16();
            if (nameLength is < 0 or > 128)
                return false;
            var name = stream.ReadString(nameLength);
            var race = (Race)stream.ReadByte();
            var gender = (Gender)stream.ReadByte();
            for (var i = 0; i < 7; i++)
                stream.ReadUInt32(); // The server selects body items from its model tables.

            var customModel = new UnitCustomModelParams();
            customModel.Read(stream, true);
            var ability = (AbilityType)stream.ReadByte();
            stream.ReadByte(); // Additional starting abilities are never accepted.
            stream.ReadByte();
            stream.ReadByte(); // The server starts every new character at level 1.
            stream.ReadInt32(); // introZoneId. The server template selects the starting zone.
            if (stream.LeftBytes != 0)
                return false;

            request = new Request(name, race, gender, customModel, ability);
            return true;
        }
        catch (MarshalException)
        {
            return false;
        }
    }

    public override void Read(PacketStream stream)
    {
        if (!Connection.IsAuthenticated || Connection.AccountId == 0)
        {
            Connection.Shutdown();
            return;
        }
        if (!TryReadRequest(stream, out var request))
        {
            Connection.SendPacket(new SCCharacterCreationFailedPacket(CharacterCreateError.ServerError));
            return;
        }
        CharacterManager.Instance.Create(Connection, request.Name, request.Race, request.Gender, [], request.CustomModel,
            request.Ability, AbilityType.None, AbilityType.None, 1);
    }
}
