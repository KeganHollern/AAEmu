using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.Shipyard;

public readonly record struct ShipyardPlacementRequest(
    uint TemplateId, Vector3 Position, float Yaw, ulong DesignItemId,
    CryBounds LocalBounds, bool AutoUseAAPoint);
