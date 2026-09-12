using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed record CryCgaNode(int Id, int ParentId, Matrix4x4 LocalTransform,
    int PositionController, int RotationController, int ScaleController)
{
    public string Name { get; init; } = "";
}

public sealed record CryCgaRenderBounds(int NodeId, CryBounds Bounds, Matrix4x4 Transform);

/// <summary>Samples the native CGA 0x826 TCB controllers and their rigid node hierarchy.</summary>
public sealed class CryCgaAnimation
{
    private readonly Dictionary<int, Track> _tracks;
    private readonly IReadOnlyList<CryCgaNode> _nodes;
    private readonly IReadOnlyList<CryCgaRenderBounds> _renderBounds;
    private readonly float _startFrame;
    private readonly float _endFrame;
    private readonly float _secondsPerFrame;

    private CryCgaAnimation(Dictionary<int, Track> tracks, IReadOnlyList<CryCgaNode> nodes,
        IReadOnlyList<CryCgaRenderBounds> renderBounds, float startFrame, float endFrame, float secondsPerFrame)
    {
        _tracks = tracks;
        _nodes = nodes;
        _renderBounds = renderBounds;
        _startFrame = startFrame;
        _endFrame = endFrame;
        _secondsPerFrame = secondsPerFrame;
    }

    public double DurationSeconds => (_endFrame - _startFrame) * _secondsPerFrame;

    public CryCgaAnimation WithClip(byte[] data) => Read(data, _nodes, _renderBounds, true);

    public static CryCgaAnimation Read(byte[] data, IReadOnlyList<CryCgaNode> nodes,
        IReadOnlyList<CryCgaRenderBounds> renderBounds, bool matchNodeNames = false)
    {
        using var reader = new BinaryReader(new MemoryStream(data, false));
        reader.BaseStream.Position = 12;
        var version = reader.ReadInt32();
        if (version is not (0x744 or 0x745))
            throw new NotSupportedException("Unsupported CGA file version.");
        var table = reader.ReadInt32();
        reader.BaseStream.Position = table;
        var count = CryPhysicsDataReader.ReadCount(reader, data.Length / 16);
        var chunks = new List<(uint Kind, int Version, int Body, int Id)>();
        for (var i = 0; i < count; i++)
        {
            var kind = reader.ReadUInt32();
            var chunkVersion = reader.ReadInt32();
            var offset = reader.ReadInt32();
            var id = reader.ReadInt32();
            if (version == 0x745)
                reader.ReadInt32();
            chunks.Add((kind, chunkVersion, offset + 16, id));
        }
        float start = 0, end = 0, secondsPerFrame = 1f / 30;
        var tracks = new Dictionary<int, Track>();
        var clipNodes = new Dictionary<string, (int Position, int Rotation, int Scale)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            reader.BaseStream.Position = chunk.Body;
            if (matchNodeNames && chunk.Kind == 0xcccc000b)
            {
                if (chunk.Version != 0x823)
                    throw new NotSupportedException("Unsupported CGA clip node version.");
                var name = System.Text.Encoding.UTF8.GetString(CryPhysicsDataReader.ReadExactly(reader, 64)).TrimEnd('\0');
                reader.BaseStream.Position = chunk.Body + 188;
                clipNodes[name] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }
            else if (chunk.Kind == 0xcccc000e)
            {
                if (chunk.Version != 0x918)
                    throw new NotSupportedException("Unsupported CGA timing chunk.");
                secondsPerFrame = CryPhysicsDataReader.ReadFloat(reader) * reader.ReadInt32();
                CryPhysicsDataReader.ReadExactly(reader, 32);
                start = reader.ReadInt32();
                end = reader.ReadInt32();
            }
            else if (chunk.Kind == 0xcccc000d)
            {
                if (chunk.Version != 0x826)
                    throw new NotSupportedException($"Unsupported CGA controller version {chunk.Version:X}.");
                var kind = reader.ReadInt32();
                var keyCount = CryPhysicsDataReader.ReadCount(reader, data.Length / 36);
                var flags = reader.ReadInt32();
                reader.ReadInt32();
                // The native loader ignores unsupported legacy Bezier and bone controllers.
                if (kind is 1 or 6)
                    continue;
                if (kind is not (9 or 10) || flags != 0 || keyCount == 0)
                    throw new NotSupportedException($"Unsupported CGA TCB controller type {kind}, flags {flags}.");
                var keys = new Key[keyCount];
                for (var i = 0; i < keyCount; i++)
                {
                    var time = reader.ReadInt32() * (1f / 160);
                    var value = CryPhysicsDataReader.ReadVector(reader);
                    var angle = kind == 10 ? CryPhysicsDataReader.ReadFloat(reader) : 0;
                    keys[i] = new Key(time, value, angle, CryPhysicsDataReader.ReadFloat(reader),
                        CryPhysicsDataReader.ReadFloat(reader), CryPhysicsDataReader.ReadFloat(reader),
                        CryPhysicsDataReader.ReadFloat(reader), CryPhysicsDataReader.ReadFloat(reader));
                    if (i > 0 && time <= keys[i - 1].Time)
                        throw new InvalidDataException("CGA TCB keys are not strictly ordered.");
                }
                tracks.Add(chunk.Id, new Track(keys, kind == 10));
            }
        }
        if (!float.IsFinite(start) || !float.IsFinite(end) || end < start || secondsPerFrame <= 0)
            throw new InvalidDataException("Invalid CGA animation range.");
        if (matchNodeNames)
        {
            var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            nodes = nodes.Select(node => assignedNames.Add(node.Name) && clipNodes.TryGetValue(node.Name, out var controls)
                    ? node with { PositionController = controls.Position, RotationController = controls.Rotation, ScaleController = controls.Scale }
                    : node with { PositionController = -1, RotationController = -1, ScaleController = -1 }).ToArray();
        }
        return new CryCgaAnimation(tracks, nodes, renderBounds, start, end, secondsPerFrame);
    }

    public CryGeometryAsset Sample(CryGeometryAsset asset, double elapsedSeconds, bool loop)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        var seconds = loop && DurationSeconds > 0 ? elapsedSeconds % DurationSeconds : Math.Min(elapsedSeconds, DurationSeconds);
        var time = _startFrame + (float)(seconds / _secondsPerFrame);
        var bind = new Dictionary<int, Matrix4x4>();
        var posed = new Dictionary<int, Matrix4x4>();
        var nodes = _nodes.ToDictionary(node => node.Id);
        foreach (var node in _nodes)
            Calculate(node.Id, []);
        var delta = new Dictionary<int, Matrix4x4>();
        foreach (var node in _nodes)
        {
            if (!Matrix4x4.Invert(bind[node.Id], out var inverse))
                throw new InvalidDataException("Singular CGA bind transform.");
            delta.Add(node.Id, inverse * posed[node.Id]);
        }
        CryBounds? bounds = null;
        foreach (var mesh in _renderBounds)
        {
            var next = mesh.Bounds.Transform(mesh.Transform * delta[mesh.NodeId]);
            bounds = bounds?.Union(next) ?? next;
        }
        if (bounds.HasValue)
        {
            var size = bounds.Value.Max - bounds.Value.Min;
            var padding = new Vector3(size.X < 0.4f ? 0.2f : 0, size.Y < 0.4f ? 0.2f : 0, size.Z < 0.4f ? 0.2f : 0);
            bounds = new CryBounds(bounds.Value.Min - padding, bounds.Value.Max + padding);
        }
        return asset with
        {
            Bounds = bounds ?? asset.Bounds,
            Parts = asset.Parts.Select(part => part.CgaNodeId < 0 ? part :
                part with { Transform = part.Transform * delta[part.CgaNodeId] }).ToArray(),
            PoseRequirements = []
        };

        void Calculate(int id, HashSet<int> chain)
        {
            if (posed.ContainsKey(id))
                return;
            if (!chain.Add(id) || !nodes.TryGetValue(id, out var node))
                throw new InvalidDataException("Invalid CGA node hierarchy.");
            var local = node.LocalTransform;
            if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position))
                throw new InvalidDataException("Unsupported CGA node transform.");
            if (_tracks.TryGetValue(node.PositionController, out var positionTrack))
                position = positionTrack.SampleVector(time) * 0.01f;
            if (_tracks.TryGetValue(node.RotationController, out var rotationTrack))
                rotation = Quaternion.Conjugate(rotationTrack.SampleRotation(time));
            if (_tracks.TryGetValue(node.ScaleController, out var scaleTrack))
                scale = scaleTrack.SampleVector(time);
            var transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(position);
            if (node.ParentId >= 0)
            {
                Calculate(node.ParentId, chain);
                bind.Add(id, local * bind[node.ParentId]);
                posed.Add(id, transform * posed[node.ParentId]);
            }
            else
            {
                bind.Add(id, local);
                posed.Add(id, transform);
            }
        }
    }

    private sealed record Key(float Time, Vector3 Value, float Angle, float Tension, float Continuity,
        float Bias, float EaseTo, float EaseFrom);

    private sealed class Track
    {
        private readonly Key[] _keys;
        private readonly Vector3[] _incoming;
        private readonly Vector3[] _outgoing;
        private readonly Quaternion[] _rotations;
        private readonly Quaternion[] _rotationIncoming;
        private readonly Quaternion[] _rotationOutgoing;

        public Track(Key[] keys, bool rotation)
        {
            _keys = keys;
            _incoming = new Vector3[keys.Length];
            _outgoing = new Vector3[keys.Length];
            _rotations = new Quaternion[keys.Length];
            _rotationIncoming = new Quaternion[keys.Length];
            _rotationOutgoing = new Quaternion[keys.Length];
            if (rotation)
                PrepareRotations();
            else
                PrepareVectors();
        }

        public Vector3 SampleVector(float time)
        {
            var (next, amount) = Find(time);
            if (next == 0)
                return _keys[0].Value;
            var square = amount * amount;
            var cube = square * amount;
            return (2 * cube - 3 * square + 1) * _keys[next - 1].Value +
                (3 * square - 2 * cube) * _keys[next].Value +
                (cube - 2 * square + amount) * _outgoing[next - 1] +
                (cube - square) * _incoming[next];
        }

        public Quaternion SampleRotation(float time)
        {
            var (next, amount) = Find(time);
            if (next == 0)
                return _rotations[0];
            return Quaternion.Normalize(Quaternion.Slerp(
                Quaternion.Slerp(_rotations[next - 1], _rotations[next], amount),
                Quaternion.Slerp(_rotationOutgoing[next - 1], _rotationIncoming[next], amount),
                2 * amount * (1 - amount)));
        }

        private (int Next, float Amount) Find(float time)
        {
            if (_keys.Length == 1 || time <= _keys[0].Time)
                return (0, 0);
            var next = 1;
            while (next < _keys.Length - 1 && time > _keys[next].Time)
                next++;
            var amount = Math.Clamp((time - _keys[next - 1].Time) / (_keys[next].Time - _keys[next - 1].Time), 0, 1);
            var from = _keys[next - 1].EaseFrom;
            var to = _keys[next].EaseTo;
            var sum = from + to;
            if (sum > 1)
            {
                from /= sum;
                to /= sum;
            }
            if (sum > 0 && amount > 0 && amount < 1)
            {
                var factor = 1 / (2 - from - to);
                amount = amount < from ? factor * amount * amount / from : amount < 1 - to ?
                    factor * (2 * amount - from) : 1 - factor * (1 - amount) * (1 - amount) / to;
            }
            return (next, amount);
        }

        private void PrepareVectors()
        {
            var last = _keys.Length - 1;
            if (last == 0)
                return;
            if (last == 1)
            {
                var difference = _keys[1].Value - _keys[0].Value;
                _outgoing[0] = (1 - _keys[0].Tension) * difference;
                _incoming[1] = (1 - _keys[1].Tension) * difference;
                return;
            }
            for (var i = 1; i < last; i++)
            {
                var key = _keys[i];
                var (before, after) = TimeWeights(i);
                var positive = 0.5f * (1 - key.Tension) * (1 + key.Bias);
                var negative = 0.5f * (1 - key.Tension) * (1 - key.Bias);
                var previous = key.Value - _keys[i - 1].Value;
                var next = _keys[i + 1].Value - key.Value;
                _incoming[i] = before * (positive * (1 - key.Continuity) * previous + negative * (1 + key.Continuity) * next);
                _outgoing[i] = after * (positive * (1 + key.Continuity) * previous + negative * (1 - key.Continuity) * next);
            }
            _outgoing[0] = 1.5f * (1 - _keys[0].Tension) * (_keys[1].Value - _keys[0].Value - _incoming[1]);
            _incoming[last] = -1.5f * (1 - _keys[last].Tension) *
                (_keys[last - 1].Value - _keys[last].Value + _outgoing[last - 1]);
        }

        private void PrepareRotations()
        {
            var previous = Quaternion.Identity;
            for (var i = 0; i < _keys.Length; i++)
            {
                var key = _keys[i];
                if (key.Angle > 2 * MathF.PI - 0.00001f)
                    throw new NotSupportedException("CGA rotation requires a multiple-revolution segment.");
                var delta = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(key.Value, key.Angle));
                previous *= delta;
                _rotations[i] = previous;
            }
            for (var i = 0; i < _keys.Length; i++)
            {
                if (_keys.Length == 1)
                    break;
                var key = _keys[i];
                var previousLog = i == 0 ? LogDifference(_rotations[0], _rotations[1]) :
                    LogDifference(_rotations[i - 1], _rotations[i]);
                var nextLog = i == _keys.Length - 1 ? previousLog : LogDifference(_rotations[i], _rotations[i + 1]);
                var (before, after) = TimeWeights(i);
                var low = 0.5f * (1 - key.Tension) * (1 - key.Continuity);
                var high = 0.5f * (1 - key.Tension) * (1 + key.Continuity);
                var positive = 1 + key.Bias;
                var negative = 1 - key.Bias;
                _rotationIncoming[i] = _rotations[i] * Exp(0.5f *
                    ((1 - low * positive * before) * previousLog - high * negative * before * nextLog));
                _rotationOutgoing[i] = _rotations[i] * Exp(0.5f *
                    (high * positive * after * previousLog + (low * negative * after - 1) * nextLog));
            }
        }

        private (float Before, float After) TimeWeights(int index)
        {
            if (index == 0 || index == _keys.Length - 1)
                return (1, 1);
            var key = _keys[index];
            var factor = 2 / (_keys[index + 1].Time - _keys[index - 1].Time);
            var before = factor * (key.Time - _keys[index - 1].Time);
            var after = factor * (_keys[index + 1].Time - key.Time);
            var continuity = MathF.Abs(key.Continuity);
            return (before + continuity * (1 - before), after + continuity * (1 - after));
        }

        private static Vector3 LogDifference(Quaternion from, Quaternion to)
        {
            if (Quaternion.Dot(from, to) < 0)
                to = -to;
            var difference = Quaternion.Conjugate(from) * to;
            var vector = new Vector3(difference.X, difference.Y, difference.Z);
            var length = vector.Length();
            return length > 0 ? vector * (MathF.Atan2(length, difference.W) / length) : Vector3.Zero;
        }

        private static Quaternion Exp(Vector3 vector)
        {
            var length = vector.Length();
            return length > 0 ? new Quaternion(vector * (MathF.Sin(length) / length), MathF.Cos(length)) : Quaternion.Identity;
        }
    }
}
