using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Xml;

namespace AAEmu.Game.Models.Game.World.Zones;

public class Area
{
    public uint Id { get; set; }
    public string Name { get; set; }
    public List<Vector3> Points { get; set; } = [];
    public float Height { get; set; }

    public bool Contains(Vector3 position)
    {
        if (!float.IsFinite(position.Z) || Points.Count < 3 || !float.IsFinite(Height) || Height < 0)
            return false;
        // r208022 CAreaManager::UpdatePlayer checks the authored vertical volume.
        // Height is not scaled. A zero height means an unbounded vertical column.
        var minimumZ = Points.Min(point => point.Z);
        if (Height > 0 && (position.Z < minimumZ || position.Z > minimumZ + Height))
            return false;
        return Point.IsInside(Points, Points.Count, position);
    }

    internal static List<Area> ReadSubZones(IEnumerable<string> paths, Func<string, string> read, XmlWorldZone zone)
    {
        var result = new List<Area>();
        foreach (var path in paths.OrderBy(path => path.Count(c => c is '/' or '\\'))
                     .ThenBy(path => path, StringComparer.Ordinal))
        {
            var document = XDocument.Parse(read(path));
            if (document.Root?.Name != "Objects")
                throw new InvalidDataException($"Subzone geometry must have an Objects root: {path}");
            foreach (var entity in document.Root.Elements("Entity"))
            {
                var offset = ReadVector((string)entity.Attribute("Pos"));
                offset += new Vector3(
                    (zone.OriginX + ((int?)entity.Attribute("cellX") ?? 0)) * WorldManager.CELL_SIZE,
                    (zone.OriginY + ((int?)entity.Attribute("cellY") ?? 0)) * WorldManager.CELL_SIZE, 0);
                var rotation = Quaternion.Identity;
                if (entity.Attribute("Rotate") is { } rotate)
                {
                    // CryEngine serializes quaternions as w,x,y,z.
                    var values = ReadFloats(rotate.Value, 4);
                    rotation = new Quaternion(values[1], values[2], values[3], values[0]);
                    if (rotation.LengthSquared() < 0.00001f)
                        throw new InvalidDataException($"Subzone geometry has a zero quaternion: {path}");
                    rotation = Quaternion.Normalize(rotation);
                }
                var scale = entity.Attribute("Scale") is { } scaleAttribute
                    ? ReadVector(scaleAttribute.Value) : Vector3.One;
                foreach (var area in entity.Elements("Area"))
                {
                    if ((int?)area.Attribute("Group") != 18)
                        continue;
                    var id = (uint?)area.Attribute("Id") ?? 0;
                    var points = area.Element("Points")?.Elements("Point")
                        .Select(point => Vector3.Transform(ReadVector((string)point.Attribute("Pos")) * scale, rotation) + offset)
                        .ToList() ?? [];
                    var height = (float?)area.Attribute("Height") ?? 0;
                    if (!float.IsFinite(height) || height < 0 || points.Count < 3 || points.Any(point =>
                            !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)))
                        throw new InvalidDataException($"Invalid subzone polygon {id}: {path}");
                    if (!result.Any(current => current.Id == id && current.Height == height && current.Points.SequenceEqual(points)))
                        result.Add(new Area { Id = id, Name = (string)entity.Attribute("Name") ?? string.Empty, Points = points, Height = height });
                }
            }
        }
        return result;
    }

    private static Vector3 ReadVector(string text)
    {
        var values = ReadFloats(text, 3);
        return new Vector3(values[0], values[1], values[2]);
    }

    private static float[] ReadFloats(string text, int count)
    {
        var parts = text?.Split(',');
        if (parts?.Length != count)
            throw new InvalidDataException("Invalid subzone geometry vector.");
        var values = parts.Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Non-finite subzone geometry vector.");
        return values;
    }
}
