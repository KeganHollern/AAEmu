using System.Numerics;
using System.Text;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Reads and samples the authored r208022 CAF controller tracks.</summary>
public sealed class CryCharacterAnimation
{
    private readonly Dictionary<uint, Track> _tracks;
    private readonly float _firstFrame;
    private readonly float _lastFrame;
    private readonly float _secondsPerFrame;

    private CryCharacterAnimation(Dictionary<uint, Track> tracks, float firstFrame, float lastFrame, float secondsPerFrame)
    {
        _tracks = tracks;
        _firstFrame = firstFrame;
        _lastFrame = lastFrame;
        _secondsPerFrame = secondsPerFrame;
    }

    public double DurationSeconds => (_lastFrame - _firstFrame) * _secondsPerFrame;

    public static CryCharacterAnimation Read(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, false));
        if (Encoding.ASCII.GetString(CryPhysicsDataReader.ReadExactly(reader, 6)) != "CryTek")
            throw new InvalidDataException("Invalid CAF signature.");
        reader.BaseStream.Position = 12;
        var version = reader.ReadInt32();
        if (version is not (0x744 or 0x745))
            throw new NotSupportedException($"Unsupported CAF version {version:X}.");
        var table = reader.ReadInt32();
        if (table < 20 || table > data.Length - 4)
            throw new InvalidDataException("Invalid CAF chunk table.");
        reader.BaseStream.Position = table;
        var count = CryPhysicsDataReader.ReadCount(reader, (data.Length - table - 4) / (version == 0x744 ? 16 : 20));
        var chunks = new List<(uint Kind, int Version, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            var kind = reader.ReadUInt32();
            var chunkVersion = reader.ReadInt32();
            var offset = reader.ReadInt32();
            reader.ReadInt32();
            if (version == 0x745)
                reader.ReadInt32();
            if (offset < 20 || offset > data.Length - 32)
                throw new InvalidDataException("Invalid CAF chunk offset.");
            chunks.Add((kind, chunkVersion, offset + 16));
        }
        var tracks = new Dictionary<uint, Track>();
        float firstFrame = 0, lastFrame = 0, secondsPerFrame = 0;
        foreach (var chunk in chunks)
        {
            reader.BaseStream.Position = chunk.Offset;
            if (chunk.Kind == 0xcccc000e)
            {
                if (chunk.Version != 0x918)
                    throw new NotSupportedException("Unsupported CAF timing chunk.");
                var secondsPerTick = CryPhysicsDataReader.ReadFloat(reader);
                var ticksPerFrame = reader.ReadInt32();
                CryPhysicsDataReader.ReadExactly(reader, 32);
                firstFrame = reader.ReadInt32();
                lastFrame = reader.ReadInt32();
                secondsPerFrame = secondsPerTick * ticksPerFrame;
            }
            else if (chunk.Kind == 0xcccc000d)
            {
                if (chunk.Version != 0x829)
                    throw new NotSupportedException($"Unsupported CAF controller version {chunk.Version:X}.");
                var id = reader.ReadUInt32();
                var rotationCount = reader.ReadUInt16();
                var positionCount = reader.ReadUInt16();
                var rotationFormat = reader.ReadByte();
                var rotationTimeFormat = reader.ReadByte();
                var positionFormat = reader.ReadByte();
                var positionKeysInfo = reader.ReadByte();
                var positionTimeFormat = reader.ReadByte();
                var aligned = reader.ReadByte() != 0;
                reader.ReadUInt16();
                var rotations = new Quaternion[rotationCount];
                for (var i = 0; i < rotations.Length; i++)
                    rotations[i] = ReadQuaternion(reader, rotationFormat);
                Align(reader, aligned);
                var rotationTimes = ReadTimes(reader, rotationTimeFormat, rotationCount);
                Align(reader, aligned);
                if (positionCount > 0 && positionFormat != 2)
                    throw new NotSupportedException("Unsupported CAF position compression.");
                var positions = new Vector3[positionCount];
                for (var i = 0; i < positions.Length; i++)
                    positions[i] = CryPhysicsDataReader.ReadVector(reader);
                Align(reader, aligned);
                var positionTimes = positionCount == 0 ? [] : positionKeysInfo == 0 ? rotationTimes :
                    ReadTimes(reader, positionTimeFormat, positionCount);
                if (positionTimes.Length != positionCount)
                    throw new InvalidDataException("CAF shared key counts differ.");
                tracks.Add(id, new Track(rotations, rotationTimes, positions, positionTimes));
            }
        }
        if (secondsPerFrame <= 0 || !float.IsFinite(secondsPerFrame) || lastFrame < firstFrame)
            throw new InvalidDataException("Invalid CAF clip timing.");
        return new CryCharacterAnimation(tracks, firstFrame, lastFrame, secondsPerFrame);
    }

    /// <summary>Samples each local bone track, then applies the skeleton parent transforms.</summary>
    public CryGeometryAsset Sample(CryGeometryAsset bindAsset, double elapsedSeconds, bool loop)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (bindAsset.CharacterBones.Count == 0)
            throw new InvalidDataException("The CAF model has no compiled skeleton.");
        var seconds = loop && DurationSeconds > 0 ? elapsedSeconds % DurationSeconds : Math.Min(elapsedSeconds, DurationSeconds);
        var frame = _firstFrame + (float)(seconds / _secondsPerFrame);
        var transforms = new Matrix4x4[bindAsset.CharacterBones.Count];
        foreach (var bone in bindAsset.CharacterBones)
        {
            var local = bone.BindTransform;
            if (bone.ParentIndex >= 0)
            {
                if (!Matrix4x4.Invert(bindAsset.CharacterBones[bone.ParentIndex].BindTransform, out var inverse))
                    throw new InvalidDataException("Invalid character bind transform.");
                local *= inverse;
            }
            if (_tracks.TryGetValue(bone.ControllerId, out var track))
            {
                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position) ||
                    Vector3.DistanceSquared(scale, Vector3.One) > 0.000001f)
                    throw new InvalidDataException("Unsupported scaled skeleton bind transform.");
                if (track.Rotations.Length > 0)
                {
                    var (index, amount) = FindKey(track.RotationTimes, frame);
                    rotation = index == 0 ? track.Rotations[0] : Quaternion.Lerp(track.Rotations[index - 1], track.Rotations[index], amount);
                }
                if (track.Positions.Length > 0)
                {
                    var (index, amount) = FindKey(track.PositionTimes, frame);
                    position = index == 0 ? track.Positions[0] : Vector3.Lerp(track.Positions[index - 1], track.Positions[index], amount);
                }
                local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            }
            transforms[bone.Index] = bone.ParentIndex < 0 ? local : local * transforms[bone.ParentIndex];
        }
        var parts = bindAsset.Parts.Select(part => part.BoneIndex < 0 ? part :
            part with { Transform = transforms[part.BoneIndex] }).ToArray();
        // This is a broad-phase bound. Native animated render bounds remain a separate requirement.
        var bounds = bindAsset.Bounds;
        foreach (var part in parts)
            bounds = bounds.Union(CryGeometryQueries.GetBounds(part, Matrix4x4.Identity));
        return bindAsset with
        {
            Bounds = bounds,
            Parts = parts,
            PoseRequirements = bindAsset.PoseRequirements.Select(pose => pose with { AffectsCollision = false })
                .Append(new CryGeometryPoseRequirement("", "", Matrix4x4.Identity, "", true, true)
                    { AffectsCollision = false }).ToArray()
        };
    }

    private static (int Index, float Amount) FindKey(float[] times, float frame)
    {
        if (frame <= times[0])
            return (0, 0);
        var index = Array.BinarySearch(times, frame);
        if (index >= 0)
            return (index, 1);
        index = ~index;
        if (index >= times.Length)
            return (times.Length - 1, 1);
        return (index, (frame - times[index - 1]) / (times[index] - times[index - 1]));
    }

    private static float[] ReadTimes(BinaryReader reader, int format, int count)
    {
        var values = new float[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = format switch
            {
                0 => CryPhysicsDataReader.ReadFloat(reader),
                1 => reader.ReadUInt16(),
                2 => reader.ReadByte(),
                _ => throw new NotSupportedException($"Unsupported CAF key time format {format}.")
            };
            if (i > 0 && values[i] <= values[i - 1])
                throw new InvalidDataException("CAF key times are not strictly ordered.");
        }
        return values;
    }

    private static Quaternion ReadQuaternion(BinaryReader reader, int format)
    {
        if (format == 1)
        {
            var vector = CryPhysicsDataReader.ReadVector(reader);
            return new Quaternion(vector, CryPhysicsDataReader.ReadFloat(reader));
        }
        if (format is not (5 or 8))
            throw new NotSupportedException($"Unsupported CAF quaternion compression {format}.");
        var packed = format == 5 ? reader.ReadUInt32() | ((ulong)reader.ReadUInt16() << 32) : reader.ReadUInt64();
        var highWord = (ushort)(packed >> 32);
        var missing = format == 5 ? highWord > 0xbfff ? 3 : highWord > 0x7999 ? 2 : highWord > 0x3999 ? 1 : 0 :
            (int)(packed >> 62);
        Span<float> values = stackalloc float[4];
        var shift = 0;
        var remainingSquare = 1f;
        for (var i = 0; i < 4; i++)
        {
            if (i == missing)
                continue;
            var bits = format == 5 ? 15 : shift < 42 ? 21 : 20;
            var divisor = format == 5 ? 23170f : bits == 21 ? 1482909f : 741454f;
            var value = (float)((packed >> shift) & ((1UL << bits) - 1));
            values[i] = (format == 5 ? value * (1f / divisor) : value / divisor) - 0.707106781186f;
            remainingSquare -= values[i] * values[i];
            shift += bits;
        }
        if (remainingSquare < 0)
            throw new InvalidDataException("Invalid compressed CAF quaternion.");
        values[missing] = MathF.Sqrt(remainingSquare);
        return new Quaternion(values[0], values[1], values[2], values[3]);
    }

    private static void Align(BinaryReader reader, bool aligned)
    {
        if (aligned)
            reader.BaseStream.Position = (reader.BaseStream.Position + 3) & ~3L;
    }

    private sealed record Track(Quaternion[] Rotations, float[] RotationTimes, Vector3[] Positions, float[] PositionTimes);
}
