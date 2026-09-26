using System.Numerics;

using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Units;

public sealed class Portal : Npc
{
    public Transform TeleportPosition { get; set; }
    public Npc LinkedPortal { get; set; }

    internal bool CanUseFrom(Character character)
    {
        var world = character?.ParentWorld;
        if (world == null || Template == null || Transform == null || character.Transform == null || ObjId == 0 ||
            !ReferenceEquals(world, ParentWorld) || Despawned || Hp <= 0 ||
            !ReferenceEquals(world.GetNpc(ObjId), this) ||
            Transform.InstanceId != character.Transform.InstanceId ||
            Transform.WorldId != character.Transform.WorldId || !float.IsFinite(Scale) || Scale <= 0)
            return false;

        // The r208022 producer at 3930cff0 compares squared origin distance with the
        // transformed model sphere radius, without squaring that radius. The portal
        // prefabs for models 308 and 667 each contain one authored sphere of radius 3.
        var radius = ModelId is 308 or 667 ? 3f * Scale : 0f;
        return radius > 0 && Vector3.DistanceSquared(character.Transform.World.Position, Transform.World.Position) <= radius;
    }

    private void KillLinkedPortal()
    {
        // Make sure to mark this portal as "dead" to avoid loops
        Hp = 0;
        // Remove the linked portal as well if it's still alive
        if (LinkedPortal is { Hp: > 0 })
        {
            LinkedPortal.Delete();
        }
    }

    public override void DoDie(BaseUnit killer, KillReason killReason)
    {
        base.DoDie(killer, killReason);
        KillLinkedPortal();
    }

    public override void Delete()
    {
        // Broadcast its kill effect to be sure it's removed 
        BroadcastPacket(new SCUnitDeathPacket(ObjId, KillReason.PortalTimeout, 0, 0, 0), false);
        // Do normal despawn handling
        base.Delete();
        KillLinkedPortal();
    }
}
