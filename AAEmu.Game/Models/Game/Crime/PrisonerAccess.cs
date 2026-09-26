using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Teleport;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Crime;

internal static class PrisonerAccess
{
    internal static void CancelInstanceAdmissions(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            InstantGameManager.Instance.CancelAdmissions(character);
            foreach (var world in WorldManager.Instance.GetWorlds())
                world.DungeonInstance?.CancelAdmission(character);
            character.MainWorldPosition = null;
        
        }
    }

    internal static bool MoveToJusticeDestination(Character character, WorldSpawnPosition destination, TeleportReason reason)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var manager = WorldManager.Instance;
            var world = manager.GetWorldsByTemplate(destination.WorldId)
                .FirstOrDefault(candidate => candidate.DungeonInstance == null && candidate.ChannelId == 0);
            if (world == null)
                return false;

            var oldWorld = character.ParentWorld;
            var crossesInstance = character.Transform.InstanceId != world.Id;
            CancelInstanceAdmissions(character);
            oldWorld?.MateManager?.RemoveAndDespawnAllActiveOwnedMates(character);
            if (crossesInstance)
            {
                WorldManager.RemoveVisibleObject(character);
                oldWorld?.RemoveObject(character);
            }

            character.DisabledSetPosition = true;
            character.Transform.ApplyWorldSpawnPosition(destination, world.Id);
            world.AddObject(character);
            if (crossesInstance)
            {
                character.SendPacket(new SCLoadInstancePacket(world.Id, destination.ZoneId,
                    destination.X, destination.Y, destination.Z, destination.Roll, destination.Pitch, destination.Yaw));
            }
            else
            {
                character.SendPacket(new SCTeleportUnitPacket(reason, 0,
                    destination.X, destination.Y, destination.Z, destination.Yaw));
            }
            return true;
        }
    }

    internal static bool CanEnter(Character character, bool battlefield = false)
    {
        if (character == null)
            return false;
        if (!character.IsPrisoner)
            return true;

        character.SendErrorMessage(battlefield
            ? ErrorMessageType.PrisonerCannotJoinBattleField
            : ErrorMessageType.CannotUsePortalInTrial);
        return false;
    }
}
