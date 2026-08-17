namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// One hand-authored quest trigger sphere from Data/Worlds/{world}/quest_spheres.json.
///
/// The client packs only contain the quest-sign hint circles
/// (level_design/zone/*/quest_sign_sphere.g), which AAEmu reuses as the
/// server-side trigger volumes for QuestActObjSphere objectives. A number of
/// quest components have no client-side sphere at all (retail used
/// server-only trigger volumes for those), leaving their sphere objective
/// impossible to fire and the quest impossible to complete
/// (aaemu-cluster#78, e.g. quest 1650 "Borrowed Bravery"). Entries in the
/// supplement file fill those gaps.
/// </summary>
public class QuestSphereSupplement
{
    /// <summary>Human-readable description of the entry; not used by code.</summary>
    public string Name { get; set; }

    /// <summary>Quest template id the sphere belongs to.</summary>
    public uint QuestId { get; set; }

    /// <summary>Quest component id whose QuestActObjSphere act this sphere completes.</summary>
    public uint ComponentId { get; set; }

    /// <summary>Zone key the sphere is located in (informational).</summary>
    public uint ZoneId { get; set; }

    /// <summary>Sphere center, in world coordinates (no zone conversion is applied).</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>Trigger radius in meters. Client hint spheres typically use 30.</summary>
    public float Radius { get; set; }
}
