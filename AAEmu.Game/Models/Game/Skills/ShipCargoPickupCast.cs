using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Skills;

internal sealed class ShipCargoPickupCast
{
    private readonly Doodad _rack;
    private readonly Slave _ship;
    private readonly ulong _itemId;
    private readonly bool _startedOnRack;
    private readonly Vector3 _start;
    private readonly Vector3 _socket;

    private ShipCargoPickupCast(Doodad rack, Slave ship, Character player)
    {
        _rack = rack;
        _ship = ship;
        _itemId = rack.ItemId;
        _startedOnRack = player.Transform.Parent == rack.Transform;
        _start = player.Transform.Local.Position;
        _socket = rack.Transform.Local.Position;
    }

    internal static ShipCargoPickupCast Start(Skill skill, BaseUnit caster, BaseUnit target)
    {
        if (skill.Id != 15309 || caster is not Character player ||
            target is not Doodad { TemplateId: 3446, FuncGroupId: 8265, ItemId: > 0, ParentObj: Slave ship } rack ||
            ship.Template?.IsABoat() != true || !ship.AttachedDoodads.Contains(rack) || rack.Transform.Parent != ship.Transform ||
            (player.Transform.Parent != rack.Transform && player.Transform.Parent != ship.Transform))
            return null;
        return new ShipCargoPickupCast(rack, ship, player);
    }

    internal bool IsSupport(Transform support) => support == _rack.Transform || support == _ship.Transform;

    internal bool CanContinue(Unit player)
    {
        var support = player.Transform.Parent;
        if (!IsSupport(support) || player.ParentWorld != _ship.ParentWorld || _rack.ParentWorld != _ship.ParentWorld ||
            _ship.IsDead || _ship.AttachmentsRetired || _rack.ItemId != _itemId || _rack.FuncGroupId != 8265 ||
            _rack.Despawn != default || _rack.ParentObj != _ship || _rack.Transform.Parent != _ship.Transform ||
            !_ship.AttachedDoodads.Contains(_rack) || _rack.Transform.Local.Position != _socket)
            return false;

        var now = player.Transform.Local.Position;
        var onRack = support == _rack.Transform;
        if (onRack == _startedOnRack)
            return SkillCastReactions.SameMovementPosition(_start, now);

        // Stock ship cargo racks share the hull's axes and scale. Compare both native
        // reports in the hull frame, anchored to the start of the cast even after more contact changes.
        if (_rack.Transform.Local.Rotation != Vector3.Zero || _rack.Scale != _ship.Scale)
            return false;
        var rack = SkillCastReactions.PackedPosition(onRack ? now : _start);
        var deck = SkillCastReactions.PackedPosition(onRack ? _start : now);
        // Z uses nearest rounding. Restore the difference in wire steps before comparing:
        // the reader rounds each decoded height to 4 decimal places.
        const double heightStep = 4196d / 4194304;
        var heightSteps = Math.Round((deck.Z - rack.Z) / heightStep);
        return OverlappingHorizontalCells(rack.X, deck.X, _socket.X) &&
               OverlappingHorizontalCells(rack.Y, deck.Y, _socket.Y) &&
               Math.Abs(heightSteps * heightStep - _socket.Z) < heightStep;
    }

    private static bool OverlappingHorizontalCells(float rack, float deck, float offset)
    {
        // ConvertPosition truncates each signed XY coordinate toward zero. Zero
        // can represent either sign. Require an intersection, not a larger movement radius.
        const double step = 8d / 4096;
        var rackOnDeck = (double)offset + rack;
        if (rackOnDeck == deck)
            return true; // The reported endpoint itself belongs to both cells, including with opposite signs.
        var rackLow = rackOnDeck - (rack <= 0 ? step : 0);
        var rackHigh = rackOnDeck + (rack >= 0 ? step : 0);
        var deckLow = (double)deck - (deck <= 0 ? step : 0);
        var deckHigh = (double)deck + (deck >= 0 ? step : 0);
        return rackLow < deckHigh && deckLow < rackHigh;
    }
}
