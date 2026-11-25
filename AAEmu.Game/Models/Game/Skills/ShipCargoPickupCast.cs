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
    private readonly Transform _startSupport;
    private readonly Vector3 _start;
    private readonly Vector3 _targetSocket;
    private readonly Vector3 _startSocket;
    private Vector3? _lastRestingHullPosition;
    // The resting Schooner contact shifted seven packed X steps and one Y step.
    // Eight horizontal steps from the original hull-local position cover that sample.
    private const float MaxRestingHullOffset = 8f / 512f;

    private ShipCargoPickupCast(Doodad rack, Slave ship, Character player)
    {
        _rack = rack;
        _ship = ship;
        _itemId = rack.ItemId;
        _startSupport = player.Transform.Parent;
        _start = player.Transform.Local.Position;
        _targetSocket = rack.Transform.Local.Position;
        _startSocket = SocketOnHull(_startSupport);
    }

    internal static ShipCargoPickupCast Start(Skill skill, BaseUnit caster, BaseUnit target)
    {
        if (skill.Id != Doodad.RetrieveTradePackSkillId || caster is not Character player ||
            target is not Doodad rack || rack.GetLoadedCargoShip() is not { } ship)
            return null;
        var support = player.Transform.Parent;
        if (support != rack.Transform && support != ship.Transform && !IsInstalledEmptyRack(support, ship))
            return null;
        return new ShipCargoPickupCast(rack, ship, player);
    }

    private static bool IsInstalledEmptyRack(Transform support, Slave ship) =>
        support?.GameObject is Doodad { TemplateId: 3446, FuncGroupId: 8264, ItemId: 0 } rack &&
        rack.Transform == support && support.Parent == ship.Transform && rack.ParentObj == ship &&
        rack.ParentWorld == ship.ParentWorld && rack.Despawn == default && ship.AttachedDoodads.Contains(rack);

    internal bool IsSupport(Transform support) =>
        support == _rack.Transform || support == _ship.Transform || IsInstalledEmptyRack(support, _ship);

    private Vector3 SocketOnHull(Transform support) =>
        support == _ship.Transform ? Vector3.Zero : support.Local.Position;

    private bool UsesHullAxes(Transform support) => support == _ship.Transform ||
        support.Local.Rotation == Vector3.Zero && support.GameObject is Doodad rack && rack.Scale == _ship.Scale;

    internal bool CanContinue(Unit player, bool observeRestingHull = false)
    {
        var support = player.Transform.Parent;
        if (observeRestingHull && (support != _ship.Transform || _startSupport != support))
            return false;
        if (!IsSupport(support) || !IsSupport(_startSupport) || SocketOnHull(_startSupport) != _startSocket ||
            player.ParentWorld != _ship.ParentWorld || _rack.ParentWorld != _ship.ParentWorld ||
            _ship.IsDead || _ship.AttachmentsRetired || _rack.ItemId != _itemId || _rack.FuncGroupId != 8265 ||
            _rack.Despawn != default || _rack.ParentObj != _ship || _rack.Transform.Parent != _ship.Transform ||
            !_ship.AttachedDoodads.Contains(_rack) || _rack.Transform.Local.Position != _targetSocket)
            return false;

        var now = player.Transform.Local.Position;
        if (support == _startSupport)
        {
            if (SkillCastReactions.SameMovementPosition(_start, now))
                return true;
            if (!observeRestingHull)
                return _lastRestingHullPosition == now;
            if (now.Z != _start.Z || Vector3.DistanceSquared(now, _start) > MaxRestingHullOffset * MaxRestingHullOffset)
                return false;

            _lastRestingHullPosition = now;
            return true;
        }

        // Either support can be a stock cargo rack. Compare both reports in one hull frame
        // only for registered, fixed racks whose local axes and scale agree with the hull.
        if (!UsesHullAxes(_startSupport) || !UsesHullAxes(support))
            return false;
        var start = SkillCastReactions.PackedPosition(_start);
        var end = SkillCastReactions.PackedPosition(now);
        var endSocket = SocketOnHull(support);
        // Z uses nearest rounding. Restore the difference in wire steps before comparing:
        // the reader rounds each decoded height to 4 decimal places.
        const double heightStep = 4196d / 4194304;
        var heightSteps = Math.Round((end.Z - start.Z) / heightStep);
        return OverlappingHorizontalCells(start.X, end.X, (double)_startSocket.X - endSocket.X) &&
               OverlappingHorizontalCells(start.Y, end.Y, (double)_startSocket.Y - endSocket.Y) &&
               Math.Abs(heightSteps * heightStep - ((double)_startSocket.Z - endSocket.Z)) < heightStep;
    }

    private static bool OverlappingHorizontalCells(float start, float end, double offset)
    {
        // ConvertPosition truncates each signed XY coordinate toward zero. Zero
        // can represent either sign. Require an intersection, not a larger movement radius.
        const double step = 8d / 4096;
        var startInEndFrame = offset + start;
        if (startInEndFrame == end)
            return true; // The reported endpoint itself belongs to both cells, including with opposite signs.
        var startLow = startInEndFrame - (start <= 0 ? step : 0);
        var startHigh = startInEndFrame + (start >= 0 ? step : 0);
        var endLow = (double)end - (end <= 0 ? step : 0);
        var endHigh = (double)end + (end >= 0 ? step : 0);
        return startLow < endHigh && endLow < startHigh;
    }
}
