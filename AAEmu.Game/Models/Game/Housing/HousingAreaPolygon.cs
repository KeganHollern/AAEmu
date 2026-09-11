using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Xml;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>The client Area.value1 identifies housing_areas, not Area.Id.</summary>
public sealed class HousingAreaPolygon
{
    public uint Id { get; init; }
    public string Name { get; init; }
    public int Priority { get; init; }
    public float Height { get; init; }
    public Vector3[] Points { get; init; } = [];

    public bool Contains(Vector3 position)
    {
        if (!IsFinite(position) || Points.Length < 3)
            return false;
        if (Height > 0)
        {
            var bottom = Points.Min(p => p.Z);
            if (position.Z < bottom || position.Z > bottom + Height)
                return false;
        }
        return Contains2D(position.X, position.Y);
    }

    public bool Contains2D(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || Points.Length < 3)
            return false;
        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var a = Points[j];
            var b = Points[i];
            var cross = ((double)x - a.X) * (b.Y - a.Y) - ((double)y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) <= 0.00001 && x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y))
                return true;
            if ((a.Y > y) != (b.Y > y) &&
                x < ((double)b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);

    public static IReadOnlyList<HousingAreaPolygon> Read(string xml, XmlWorldZone zone)
    {
        var result = new List<HousingAreaPolygon>();
        var document = XDocument.Parse(xml);
        if (document.Root?.Name != "Objects")
            throw new InvalidDataException("Housing geometry must have an Objects root.");
        foreach (var entity in document.Root.Elements("Entity"))
        {
            var offset = ReadVector((string)entity.Attribute("Pos"));
            offset += new Vector3(
                (zone.OriginX + ReadInt(entity, "cellX")) * WorldManager.CELL_SIZE,
                (zone.OriginY + ReadInt(entity, "cellY")) * WorldManager.CELL_SIZE, 0);
            var rotation = Quaternion.Identity;
            if (entity.Attribute("Rotate") is { } rotate)
            {
                // CryEngine serializes quaternions as w,x,y,z.
                var values = ReadFloats(rotate.Value, 4);
                rotation = new Quaternion(values[1], values[2], values[3], values[0]);
                if (rotation.LengthSquared() < 0.00001f)
                    throw new InvalidDataException("Housing geometry has a zero quaternion.");
                rotation = Quaternion.Normalize(rotation);
            }
            var scale = entity.Attribute("Scale") is { } scaleAttribute
                ? ReadVector(scaleAttribute.Value) : Vector3.One;
            foreach (var area in entity.Elements("Area"))
            {
                var id = (uint?)area.Attribute("value1") ?? 0;
                // Unbound editor shapes are not housing permits.
                if (id == 0)
                    continue;
                var points = area.Element("Points")?.Elements("Point")
                    .Select(p => Vector3.Transform(ReadVector((string)p.Attribute("Pos")) * scale, rotation) + offset)
                    .ToArray() ?? [];
                var height = (float?)area.Attribute("Height") ?? 0;
                if (points.Length < 3 || points.Any(p => !IsFinite(p)) || !float.IsFinite(height) || height < 0)
                    throw new InvalidDataException($"Invalid housing polygon {id}.");
                result.Add(new HousingAreaPolygon
                {
                    Id = id,
                    Name = (string)entity.Attribute("Name") ?? string.Empty,
                    Priority = ReadInt(area, "Priority"),
                    Height = height,
                    Points = points
                });
            }
        }
        return result;
    }

    private static int ReadInt(XElement node, string name) => (int?)node.Attribute(name) ?? 0;

    private static Vector3 ReadVector(string text)
    {
        var values = ReadFloats(text, 3);
        return new Vector3(values[0], values[1], values[2]);
    }

    private static float[] ReadFloats(string text, int count)
    {
        var parts = text?.Split(',');
        if (parts?.Length != count)
            throw new InvalidDataException("Invalid housing geometry vector.");
        var values = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("Housing geometry contains a non-finite coordinate.");
        return values;
    }
}
