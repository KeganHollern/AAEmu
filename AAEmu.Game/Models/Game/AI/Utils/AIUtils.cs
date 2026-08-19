using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.AI.Utils;

public static class AiUtils
{
    /// <summary>
    /// Vertical slack allowed when deciding that a unit reached an authored path waypoint.
    /// aaemu-cluster#92: waypoint advance is a horizontal test, because server and client can legitimately
    /// disagree on the exact floor height (nav node vs. brush collision, terrain rounding). The tolerance
    /// only exists to refuse an advance when the unit sits on a completely different level.
    /// </summary>
    public const float AuthoredPathVerticalTolerance = 4f;

    /// <summary>
    /// Largest terrain correction accepted for an authored path waypoint, mirroring
    /// NpcSpawnerNpc.CreateRuntimeSpawnPosition.
    /// </summary>
    private const float MaxAuthoredPathTerrainCorrection = 1f;

    /// <summary>
    /// Resolves the Z to use for one step along an authored <c>.path</c> route.
    /// aaemu-cluster#92: the recorded waypoint Z is where the route author actually stood, so it wins.
    /// Indoors the geodata resolver answers with the nearest voxelized BAI navigation node, which can sit
    /// up to ~1m above the real brush floor; driving an NPC onto it makes the client bob the model (client
    /// grounds it, server re-asserts nav Z) and leaves a permanent Z error that keeps the final waypoint
    /// out of reach. Outdoor recorded routes still get their terrain correction, because there the
    /// rendered terrain is the authoritative surface at the waypoint's XY.
    /// </summary>
    /// <param name="npc">Unit walking the route</param>
    /// <param name="x">Step X</param>
    /// <param name="y">Step Y</param>
    /// <param name="authoredZ">Authored (or interpolated authored) Z for this step</param>
    public static float ResolveAuthoredPathHeight(Npc npc, float x, float y, float authoredZ)
    {
        if (npc == null)
            return authoredZ;

        // Missing or garbage authored data: fall back to the generic resolver
        if (!float.IsFinite(authoredZ))
            return WorldManager.Instance.GetReferenceHeight(npc.Ai, x, y, npc.Transform.World.Position.Z,
                npc.Transform.ZoneId);

        // Flying units are never grounded, their authored height is all we have
        if (npc.CanFly)
            return authoredZ;

        var geoData = npc.ParentWorld?.Template?.GeoData;
        if (geoData == null)
            return authoredZ;

        if (geoData.TryGetGroundSurface(new Vector3(x, y, authoredZ), out var surface) &&
            surface is { Source: GroundSurfaceSource.Terrain } &&
            Math.Abs(authoredZ - surface.Height) < MaxAuthoredPathTerrainCorrection)
            return surface.Height;

        return authoredZ;
    }

    /// <summary>
    /// Horizontal arrival test for authored path waypoints, with a generous vertical guard.
    /// aaemu-cluster#92: a 3D test can be defeated forever by a sub-meter Z disagreement, which stalled
    /// scripted walks on their last waypoint and left the driving command set (and its final UseSkill,
    /// usually the self-despawn) unexecuted.
    /// </summary>
    public static bool HasReachedPathWaypoint(Vector3 position, Vector3 waypoint, float arrivalRadius,
        float verticalTolerance = AuthoredPathVerticalTolerance)
    {
        return MathUtil.CalculateDistance(position, waypoint) < arrivalRadius &&
               Math.Abs(position.Z - waypoint.Z) < verticalTolerance;
    }

    // This is taken from x2ai.lua
    public static Vector3 CalcNextRoamingPosition(NpcAi ai)
    {
        var maxRoamingDistance = 6;
        var newPosition = new Vector3(
            (Random.Shared.NextSingle() - 0.5f) * maxRoamingDistance * 2 + ai.IdlePosition.X,
            (Random.Shared.NextSingle() - 0.5f) * maxRoamingDistance * 2 + ai.IdlePosition.Y,
            ai.IdlePosition.Z);

        // Get terrain height at new position
        newPosition.Z = WorldManager.Instance.GetReferenceHeight(ai, newPosition.X, newPosition.Y, newPosition.Z, ai.Owner.Transform.ZoneId);

        return newPosition;
    }

    public static NpcAi GetAiByType(AiParamType type, Npc owner)
    {
        switch (type)
        {
            case AiParamType.AlmightyNpc:
                return new AlmightyNpcAiCharacter { Owner = owner };
            case AiParamType.ArcherHoldPosition:
                return new ArcherHoldPositionAiCharacter { Owner = owner };
            case AiParamType.ArcherRoaming:
                return new ArcherRoamingAiCharacter { Owner = owner };
            case AiParamType.BigMonsterRoaming:
                return new BigMonsterRoamingAiCharacter { Owner = owner };
            case AiParamType.BigMonsterHoldPosition:
                return new BigMonsterHoldPositionAiCharacter { Owner = owner };
            case AiParamType.Default:
                return new DefaultAiCharacter { Owner = owner };
            case AiParamType.Dummy:
                return new DummyAiCharacter { Owner = owner };
            case AiParamType.Flytrap:
                return new FlytrapAiCharacter { Owner = owner };
            case AiParamType.HoldPosition:
                return new HoldPositionAiCharacter { Owner = owner };
            case AiParamType.Roaming:
                return new RoamingAiCharacter { Owner = owner };
            case AiParamType.TowerDefenseAttacker:
                return new TowerDefenseAttackerAiCharacter { Owner = owner };
            case AiParamType.WildBoarHoldPosition:
                return new WildBoarHoldPositionAiCharacter { Owner = owner };
            case AiParamType.WildBoarRoaming:
                return new WildBoarRoamingAiCharacter { Owner = owner };
            default:
                return null;
        }
    }
}
