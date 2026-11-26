using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;

using NLog;

namespace AAEmu.Game.Models.Game.World;

internal interface IWorldEntrySphere
{
    Region Region { get; set; }
    Vector3 Center { get; }
    Spheres.Spheres Definition { get; }
    bool Contains(Vector3 position);
}

internal sealed class WorldSkillSphere(Spheres.Spheres definition, uint skillId, Vector3 center, float radius) : IWorldEntrySphere
{
    private sealed class Placement
    {
        public uint SphereId { get; init; }
        public float Radius { get; init; }
        public List<Vector3> Centers { get; init; } = [];
    }

    public Region Region { get; set; }
    public Spheres.Spheres Definition { get; } = definition;
    public uint SkillId { get; } = skillId;
    public Vector3 Center { get; } = center;
    public float Radius { get; } = radius;
    public bool Contains(Vector3 position) => Vector3.DistanceSquared(position, Center) <= Radius * Radius;

    internal static List<WorldSkillSphere> Load(string world)
    {
        var spheres = new List<WorldSkillSphere>();
        var path = Path.Combine(FileManager.AppPath, "Data", "Worlds", world, "skill_spheres.json");
        if (!File.Exists(path))
            return spheres;

        var log = LogManager.GetCurrentClassLogger();
        if (!JsonHelper.TryDeserializeObject(FileManager.GetFileContents(path), out List<Placement> placements, out var error))
        {
            log.Error(error, "Could not load skill spheres from {0}.", path);
            return spheres;
        }

        foreach (var placement in placements)
        {
            var definition = SphereGameData.Instance.GetSphere(placement.SphereId);
            var skillId = SphereGameData.Instance.GetGuaranteedEntrySkill(definition);
            if (skillId == 0 || !float.IsFinite(placement.Radius) || placement.Radius <= 0)
            {
                log.Warn("Unsupported skill sphere {0} in {1}.", placement.SphereId, path);
                continue;
            }

            foreach (var center in placement.Centers)
                spheres.Add(new WorldSkillSphere(definition, skillId, center, placement.Radius));
        }
        return spheres;
    }
}
