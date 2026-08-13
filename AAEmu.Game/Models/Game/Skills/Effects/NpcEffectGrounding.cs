using System.Numerics;

using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Models.Game.Skills.Effects;

internal static class NpcEffectGrounding
{
    internal static float ResolveHeight(Npc npc, Vector3 endpoint, float tolerance)
    {
        if (npc.CanFly || npc.ParentWorld?.Template.GeoData is not { } geoData)
            return endpoint.Z;

        if (!geoData.TryGetGroundHeight(endpoint, out var groundZ) ||
            !(Math.Abs(endpoint.Z - groundZ) < tolerance))
            return endpoint.Z;

        return groundZ;
    }
}
