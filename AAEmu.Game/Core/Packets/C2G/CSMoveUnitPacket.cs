using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Teleport;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

using System.Numerics;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSMoveUnitPacket() : GamePacket(CSOffsets.CSMoveUnitPacket, 1)
{
    public override PacketLogLevel LogLevel => PacketLogLevel.Off;

    private uint _objId;
    private MoveType _moveType;

    public override void Read(PacketStream stream)
    {
        _objId = stream.ReadBc();

        var type = (MoveTypeEnum)stream.ReadByte();
        _moveType = MoveType.GetType(type);
        stream.Read(_moveType);
    }

    public override void Execute()
    {
        // _moveType.Flags
        // 0x02 : Moving
        // 0x04 : Stopping (released movement keys)
        // 0x06 : Jumping
        // 0x40 : Standing on something
        /*
        Logger.Debug("CSMoveUnitPacket(" + _moveType.Type + ") \nScType: " + _moveType.ScType + " - Flags: " +
                   _moveType.Flags.ToString("X") + " - " +
                   "Phase: " + _moveType.Phase + " - Time: " + _moveType.Time + " - " +
                   "Sender: " + Connection.ActiveChar.Name + " (" + Connection.ActiveChar.ObjId + ") - " +
                   "Obj: " + (WorldManager.Instance.GetBaseUnit(_objId)?.Name ?? "<null>") + " (" + _objId +
                   ") \n" +
                   "XYZ: " + _moveType.X.ToString("F1") + " , " + _moveType.Y.ToString("F1") + " , " +
                   _moveType.Z.ToString("F1") + " - " +
                   "Rot: " + _moveType.RotationX.ToString() + " , " + _moveType.RotationY.ToString() + " , " +
                   _moveType.RotationZ.ToString() + " - " +
                   "VelXYZ: " + _moveType.VelX.ToString("F1") + " , " + _moveType.VelY.ToString("F1") + " , " +
                   _moveType.VelZ.ToString("F1")
        );
        */

        var character = Connection.ActiveChar;

        if (character == null) return;
        character.LastPacketActivityTime = DateTime.UtcNow;

        // if movement is forbidden when teleporting to instances, then to exit
        if (character.DisabledSetPosition) return;

        var targetUnit = character.ParentWorld.GetBaseUnit(_objId);

        // Invalid Object ?
        if (targetUnit == null)
        {
            // TODO по какой то причине объект удалили из региона, наверное нужно его как то вернуть назад 
            // TODO for some reason the object has been removed from the region, you probably need to get it back somehow
            Logger.Warn($"Invalid target {_objId} from {character.Name}");
            return;
        }

        // We are not controlling our main character
        switch (_moveType)
        {
            case ShipRequestMoveType srmt:
                {
                    // We are controlling a ship
                    // Logger.Debug("ShipRequestMoveType - Throttle: {0} - Steering {1}", srmt.Throttle, srmt.Steering);
                    if (targetUnit is not Slave ship)
                        return;

                    // Only the character attached to the driver seat may steer.
                    if (!IsSlaveDriver(ship, character))
                        return;

                    ship.ThrottleRequest = srmt.Throttle;
                    ship.SteeringRequest = srmt.Steering;

                    // Make sure driver is attached to the ship
                    character.Transform.Parent = ship.Transform;
                    // Actual movement and sending of packets is handle by the Physics Engine
                    break;
                }
            case VehicleMoveType vmt:
                {
                    // Steering: Value between -1.0 and +1.0
                    // WheelAngVel: Velocity on individual wheels? (note: cart/wagon has "no wheels")
                    /*
                    Logger.Debug("VehicleMoveType AngleVelocity XYZ: " + vmt.AngVelX.ToString("F1") + " , " +
                               vmt.AngVelY.ToString("F1") + " , " + vmt.AngVelZ.ToString("F1") + "\n" +
                               "Steering: " + vmt.Steering + " - WheelAngleVelocity: (" +
                               string.Join(" , ", vmt.WheelAngVel.ToArray()) + " )");
                    */

                    if (targetUnit is not Slave car)
                        return;

                    // Only the character attached to the driver seat may drive.
                    if (!IsSlaveDriver(car, character))
                        return;

                    // Vehicles are root objects, so their local position is their world position.
                    var claimedCarPosition = new Vector3(vmt.X, vmt.Y, vmt.Z);
                    if (!MovementValidation.TryAccept(car, character, claimedCarPosition, "vehicle"))
                        return;

                    var (rotDegX, rotDegY, rotDegZ) = MathUtil.GetSlaveRotationInDegrees(vmt.RotationX, vmt.RotationY, vmt.RotationZ);

                    // Make sure driver is attached to car
                    character.Transform.Parent = car.Transform;
                    car.Transform.Local.SetPosition(vmt.X, vmt.Y, vmt.Z, rotDegX, rotDegY, rotDegZ);
                    car.BroadcastPacket(new SCOneUnitMovementPacket(_objId, vmt), true);
                    car.Transform.FinalizeTransform(); // Propagate position updates to all children
                    break;
                }
            case UnitMoveType dmt:
                {
                    // Logger.Debug($"{targetUnit.Name} => ActorFlags: 0x{dmt.ActorFlags:X} - ClimbData: {dmt.ClimbData:X} - GcId: {dmt.GcId}");

                    // Its moving Pets, handle Pet XP for moving
                    if (targetUnit is Mate mate)
                    {
                        // Only the mate's owner may author its movement.
                        if (mate.OwnerObjId != character.ObjId)
                        {
                            Logger.Warn("{Character} tried to move a mate ({ObjId}) they do not own", character.Name, mate.ObjId);
                            return;
                        }

                        // Mates are root objects; validate the claimed position before applying it.
                        var claimedMatePosition = new Vector3(dmt.X, dmt.Y, dmt.Z);
                        if (!MovementValidation.TryAccept(mate, character, claimedMatePosition, "mate",
                                dmt.Flags.HasFlag(MoveTypeFlags.HasScTypeAndPhase)))
                            return;

                        // Pet moved
                        RemoveEffects(targetUnit, _moveType);

                        if (dmt.VelX != 0 || dmt.VelY != 0)
                            mate.StartUpdateXp(character);
                        else
                            mate.StopUpdateXp();

                        foreach (var (_, passengerInfo) in mate.Passengers)
                        {
                            var passenger = WorldManager.Instance.GetCharacterByObjId(passengerInfo._objId);
                            if (passenger != null)
                            {
                                // passenger.Transform = mate.Transform.CloneDetached(passenger);
                                RemoveEffects(passenger, _moveType);
                            }
                        }
                    }

                    // If controlling character, but it's riding something, sync parent with the mount
                    if (targetUnit is Character player)
                    {
                        // A client may only author its own character's movement.
                        // (Telekinesis-style remote movement is not implemented; do not allow hijacks.)
                        if (player.ObjId != character.ObjId)
                        {
                            Logger.Warn("{Character} tried to move another character ({ObjId})", character.Name, player.ObjId);
                            return;
                        }

                        // We moved
                        RemoveEffects(player, _moveType);

                        if (player.IsRiding)
                        {
                            // Если мы сидим на питомце и Parent = null, насильно спешиваем персонажа для предотвращения сбоя клиента
                            // If we are sitting on a pet and Parent = null, we force it on there to prevent client crashing
                            if (player.Transform.Parent == null)
                            {
                                var mate2 = Connection.ActiveChar.ParentWorld.MateManager.GetActiveMates(character.Id).FirstOrDefault();
                                if (mate2 != null)
                                {
                                    player.Transform.Parent = mate2.Transform;
                                }
                            }
                            // We're riding a pet, we don't care about the rest of this function
                            // If we're riding the pet, we should only care about the pet's movement
                            Logger.Debug($"{targetUnit.Name} IsRiding, ignoring movement request");
                            return;
                        }

                        // Player moved
                        player.SetPlayerMoved();
                    }

                    var isStandingOnObject = dmt.Flags.HasFlag(MoveTypeFlags.StandingOnObject);
                    // Don't know why, but we need to Ignore GcId 1, it probably has some special meaning like "current parent"
                    var parentObject = isStandingOnObject && dmt.GcId > 1
                        ? character.ParentWorld.GetBaseUnit(dmt.GcId)
                        : null;
                    var isSticky = ((MoveTypeActorFlags)dmt.ActorFlags).HasFlag(MoveTypeActorFlags.HangingFromObject);

                    if (targetUnit.Transform.Parent != null && parentObject == null)
                    {
                        // No longer standing on object?
                        var oldParentObj = targetUnit.Transform.Parent.GameObject?.ObjId ?? 0;
                        targetUnit.Transform.Parent = null;

                        character.SendDebugMessage(
                            $"|cFF884444{targetUnit.Name} ({targetUnit.ObjId}) no longer standing on Object {oldParentObj} " +
                            $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
                    }
                    else if (targetUnit.Transform.Parent == null && parentObject != null)
                    {
                        // Standing on a new object ?
                        targetUnit.Transform.Parent = parentObject.Transform;

                        character.SendDebugMessage(
                            $"|cFF448844{targetUnit.Name} ({targetUnit.ObjId}) standing on Object {parentObject.Name} ({parentObject.ObjId}) " +
                            $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
                    }
                    else if (targetUnit.Transform.Parent is { GameObject: not null } &&
                             parentObject != null &&
                             targetUnit.Transform.Parent.GameObject.ObjId != parentObject.ObjId)
                    {
                        // Changed to standing on different object ?
                        targetUnit.Transform.Parent = parentObject.Transform;

                        character.SendDebugMessage(
                            $"|cFF448888{targetUnit.Name} ({targetUnit.ObjId}) moved to standing on new Object {parentObject.Name} ({parentObject.ObjId}) " +
                            $"@ x{dmt.X:F1} y{dmt.Y:F1} z{dmt.Z:F1} || World: {targetUnit.Transform.World}|r");
                    }

                    // If ActorFlag 0x40 is no longer set, it means we're no longer climbing/holding onto something
                    if (targetUnit.Transform.StickyParent != null && !isSticky && !IsBoardedOnTransfer(targetUnit))
                        targetUnit.Transform.StickyParent = null;

                    // Debug Climb Data
                    /*
                    if (dmt.ClimbData != 0)
                    {
                        var stickyVerticalOffset =
                            (float)(dmt.ClimbData & 0x1FFF); // / 8192f * 100f; // 13 bits
                        var stickyHorizontalOffset =
                            (float)((dmt.ClimbData & 0x00FFE000) >> 13); // / 256f * 100f; // 11 bits
                        var stickyRotationOffset =
                            (float)((sbyte)((dmt.ClimbData & 0xFF000000) >> 24)) / 254f * 360f; // 8 bits
                        Logger.Debug(
                            "ClimbData - {0} ({1}) - Vertical: {2}/8192 , Horizontal: {3}/2048, Rotation: {4}°",
                            targetUnit.Name, targetUnit.ObjId,
                            stickyVerticalOffset, stickyHorizontalOffset, stickyRotationOffset.ToString("F1"));
                    }
                    */

                    // Clients may only author movement for themselves or their own mates;
                    // anything else (other players, NPCs, slaves) must not be moved from here.
                    if (targetUnit != character && targetUnit is not Mate)
                    {
                        Logger.Warn("{Character} tried to move {UnitType} ({ObjId}) they do not control",
                            character.Name, targetUnit.GetType().Name, targetUnit.ObjId);
                        return;
                    }

                    // Validate self-propelled movement before applying it. Attached units
                    // (standing on vehicles/platforms) are carried and refreshed instead.
                    if (targetUnit == character)
                    {
                        var claimedPosition = new Vector3(dmt.X, dmt.Y, dmt.Z);
                        if (!MovementValidation.TryAccept(character, character, claimedPosition, "character",
                                dmt.Flags.HasFlag(MoveTypeFlags.HasScTypeAndPhase)))
                        {
                            // Snap the authoring client back to the authoritative position.
                            RubberbandCharacter(character);
                            return;
                        }
                    }

                    // Actually update the position
                    targetUnit.Transform.Local.SetPosition(dmt.X, dmt.Y, dmt.Z,
                        (float)MathUtil.ConvertDirectionToRadian(dmt.RotationX),
                        (float)MathUtil.ConvertDirectionToRadian(dmt.RotationY),
                        (float)MathUtil.ConvertDirectionToRadian(dmt.RotationZ));
                    //Logger.Info($"SetPosition:World {targetUnit.ObjId} is moving X={targetUnit.Transform.World.Position.X} Y={targetUnit.Transform.World.Position.Y}");
                    //Logger.Info($"SetPosition:Local {targetUnit.ObjId} is moving X={dmt.X} Y={dmt.Y}");
                    // The controlling client already integrates its own movement locally. Echoing a
                    // skill-controller move (Flags.HasScTypeAndPhase) back to that same character can
                    // interrupt the local controller and leave it in an invalid movement state.
                    // Preserve the original 1.2 behavior: observers receive the move, while a different
                    // controlled character still receives movement authored on its behalf.
                    targetUnit.BroadcastPacket(
                        new SCOneUnitMovementPacket(_objId, dmt),
                        ShouldIncludeTargetCharacter(character, targetUnit));
                    targetUnit.Transform.FinalizeTransform();

                    // Handle Fall Velocity
                    if (dmt.FallVel > 0 && targetUnit is Unit unit)
                    {
                        _ = unit.DoFallDamage(dmt.FallVel);
                        // character.SendMessage("{0} took {1} fall damage {2}/{3} HP left", unit.Name, fallDmg, unit.Hp, unit.MaxHp);
                    }

                    break;
                }
            default:
                Logger.Warn($"Unknown MoveType: {_moveType} by {character.Name} for {targetUnit.Name}");
                break;
        }
    }

    private static void RemoveEffects(BaseUnit unit, MoveType moveType)
    {
        if (moveType.VelX != 0 || moveType.VelY != 0 || moveType.VelZ != 0)
            unit.Buffs.TriggerRemoveOn(BuffRemoveOn.Move);
    }

    private static bool IsSlaveDriver(Slave slave, Character character)
    {
        if (slave.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var driver) &&
            driver.ObjId == character.ObjId)
            return true;

        Logger.Warn("{Character} tried to control slave {ObjId} without being in the driver seat", character.Name, slave.ObjId);
        return false;
    }

    /// <summary>
    /// Forces the authoring client back to the server's authoritative position after
    /// a rejected movement request. Uses the same flow as portal teleports so the
    /// client acknowledges with CSTeleportEndedPacket, which re-enables movement.
    /// </summary>
    private static void RubberbandCharacter(Character character)
    {
        var world = character.Transform.World;
        character.DisabledSetPosition = true;
        MovementValidation.Reset(character.ObjId);
        character.SendPacket(new SCTeleportUnitPacket(TeleportReason.Lockup, 0,
            world.Position.X, world.Position.Y, world.Position.Z,
            world.Rotation.Z.DegToRad()));
    }

    internal static bool ShouldIncludeTargetCharacter(Character movementAuthor, BaseUnit targetUnit)
    {
        return targetUnit is Character && targetUnit.ObjId != movementAuthor.ObjId;
    }

    private static bool IsBoardedOnTransfer(BaseUnit unit)
    {
        return unit is Character character &&
               unit.Transform.StickyParent?.GameObject is Transfer transfer &&
               transfer.AttachedCharacters.Contains(character);
    }

    public override string Verbose()
    {
        return " - " + (_moveType?.Type.ToString() ?? "none") + " " + (Connection.ActiveChar.ParentWorld.GetGameObject(_objId)?.DebugName() ?? "(" + _objId + ")");
    }
}
