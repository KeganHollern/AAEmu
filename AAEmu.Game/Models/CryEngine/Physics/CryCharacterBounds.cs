using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Applies the r208022 SSkeletonPose bounds rules to authored bone positions.</summary>
public static class CryCharacterBounds
{
    public static CryBounds FromPose(IReadOnlyList<Matrix4x4> transforms, IReadOnlyList<int> selectedBones = null)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var index in selectedBones ?? Enumerable.Range(0, transforms.Count).ToArray())
        {
            if (index < 0 || index >= transforms.Count)
                throw new InvalidDataException("Character bounds refer to an absent bone.");
            min = Vector3.Min(min, transforms[index].Translation);
            max = Vector3.Max(max, transforms[index].Translation);
        }
        return Normalize(new CryBounds(min, max));
    }

    public static CryBounds Normalize(CryBounds bounds)
    {
        var min = bounds.Min;
        var max = bounds.Max;
        var size = max - min;
        var padding = new Vector3(size.X < 0.4f ? 0.2f : 0, size.Y < 0.4f ? 0.2f : 0, size.Z < 0.4f ? 0.2f : 0);
        min -= padding;
        max += padding;
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z ||
            Vector3.Abs(min).X > 13000 || Vector3.Abs(min).Y > 13000 || Vector3.Abs(min).Z > 13000 ||
            Vector3.Abs(max).X > 13000 || Vector3.Abs(max).Y > 13000 || Vector3.Abs(max).Z > 13000)
            return new CryBounds(new Vector3(-2), new Vector3(2));
        return new CryBounds(min, max);
    }

    public static IReadOnlyList<int> ReadSubsetBones(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, false));
        var flags = reader.ReadUInt32();
        var count = CryPhysicsDataReader.ReadCount(reader, data.Length / 36);
        reader.ReadInt64();
        if ((flags & 2) == 0)
            return [];
        if (16L + count * (36L + 260) > data.Length)
            throw new InvalidDataException("Invalid character subset bone palettes.");
        reader.BaseStream.Position = 16L + count * 36L;
        var bones = new HashSet<int>();
        for (var subset = 0; subset < count; subset++)
        {
            var used = CryPhysicsDataReader.ReadCount(reader, 128);
            for (var i = 0; i < 128; i++)
            {
                var index = reader.ReadUInt16();
                if (i < used)
                    bones.Add(index);
            }
        }
        return bones.Order().ToArray();
    }
}
