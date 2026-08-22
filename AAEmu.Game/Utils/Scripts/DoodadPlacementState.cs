using System.Globalization;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AAEmu.Game.Utils.Scripts;

public sealed class DoodadPlacementSession
{
    public DoodadPlacementSession(Character editor, uint instanceId, uint objId, uint previewObjId,
        Guid sourceGuid, uint templateId, DoodadPlacementSnapshot original, DoodadSpawner sourceSpawner)
    {
        if (objId == previewObjId)
            throw new ArgumentException("Detached preview ObjId must differ from the authoritative doodad ObjId",
                nameof(previewObjId));

        Editor = editor;
        InstanceId = instanceId;
        ObjId = objId;
        PreviewObjId = previewObjId;
        SourceGuid = sourceGuid;
        TemplateId = templateId;
        Original = original;
        Preview = original;
        SourceSpawner = sourceSpawner;
    }

    public Character Editor { get; }
    public uint InstanceId { get; }
    public uint ObjId { get; }
    public uint PreviewObjId { get; }
    public Guid SourceGuid { get; }
    public uint TemplateId { get; }
    public DoodadPlacementSnapshot Original { get; }
    public DoodadPlacementSnapshot Preview { get; set; }
    public DoodadSpawner SourceSpawner { get; }
    public DoodadPlacementSubscription Subscription { get; set; }
    public Stack<DoodadPlacementSnapshot> Undo { get; } = new();
}

public sealed class DoodadPlacementSubscription(Action onDisposed) : IDisposable
{
    private Action _onDisposed = onDisposed;

    public void Cancel()
    {
        Interlocked.Exchange(ref _onDisposed, null);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
    }
}

public readonly record struct DoodadPlacementSnapshot(
    Vector3 Position,
    Vector3 Rotation,
    float Scale,
    uint FuncGroupId)
{
    private const float MinimumScale = 0.01f;
    private const float MaximumScale = 100f;
    private const float MinimumHorizontalPosition = -32768f;
    private const float MaximumHorizontalPosition = 32768f;
    private const float MinimumVerticalPosition = -100f;
    private const float MaximumVerticalPosition = 4096f;

    public static DoodadPlacementSnapshot Capture(Doodad doodad)
    {
        return new DoodadPlacementSnapshot(
            doodad.Transform.World.Position,
            doodad.Transform.World.Rotation,
            doodad.Scale,
            doodad.FuncGroupId);
    }

    public DoodadPlacementSnapshot Nudge(string axis, float delta)
    {
        return axis.ToLowerInvariant() switch
        {
            "x" => this with { Position = new Vector3(Position.X + delta, Position.Y, Position.Z) },
            "y" => this with { Position = new Vector3(Position.X, Position.Y + delta, Position.Z) },
            "z" or "up" => this with { Position = new Vector3(Position.X, Position.Y, Position.Z + delta) },
            "scale" => this with { Scale = Scale + delta },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Nudge axis must be x, y, z, or scale")
        };
    }

    public DoodadPlacementSnapshot RotateDegrees(string axis, float deltaDegrees)
    {
        return axis.ToLowerInvariant() switch
        {
            "roll" or "r" => this with
            {
                Rotation = new Vector3(
                    NormalizeDegrees(Rotation.X.RadToDeg() + deltaDegrees).DegToRad(),
                    Rotation.Y,
                    Rotation.Z)
            },
            "pitch" or "p" => this with
            {
                Rotation = new Vector3(
                    Rotation.X,
                    NormalizeDegrees(Rotation.Y.RadToDeg() + deltaDegrees).DegToRad(),
                    Rotation.Z)
            },
            "yaw" => this with
            {
                Rotation = new Vector3(
                    Rotation.X,
                    Rotation.Y,
                    NormalizeDegrees(Rotation.Z.RadToDeg() + deltaDegrees).DegToRad())
            },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Rotation axis must be roll, pitch, or yaw")
        };
    }

    public DoodadPlacementSnapshot SetValue(string axis, float value)
    {
        return axis.ToLowerInvariant() switch
        {
            "x" => this with { Position = new Vector3(value, Position.Y, Position.Z) },
            "y" => this with { Position = new Vector3(Position.X, value, Position.Z) },
            "z" => this with { Position = new Vector3(Position.X, Position.Y, value) },
            "roll" or "r" => this with
            {
                Rotation = new Vector3(NormalizeDegrees(value).DegToRad(), Rotation.Y, Rotation.Z)
            },
            "pitch" or "p" => this with
            {
                Rotation = new Vector3(Rotation.X, NormalizeDegrees(value).DegToRad(), Rotation.Z)
            },
            "yaw" => this with
            {
                Rotation = new Vector3(Rotation.X, Rotation.Y, NormalizeDegrees(value).DegToRad())
            },
            "scale" => this with { Scale = value },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Set axis must be x, y, z, roll, pitch, yaw, or scale")
        };
    }

    public bool IsValid()
    {
        return float.IsFinite(Position.X) && Position.X is > MinimumHorizontalPosition and < MaximumHorizontalPosition &&
               float.IsFinite(Position.Y) && Position.Y is > MinimumHorizontalPosition and < MaximumHorizontalPosition &&
               float.IsFinite(Position.Z) && Position.Z is >= MinimumVerticalPosition and < MaximumVerticalPosition &&
               float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) &&
               float.IsFinite(Scale) && Scale is >= MinimumScale and <= MaximumScale;
    }

    public void ApplyTo(Doodad doodad)
    {
        doodad.Transform.Local.SetPosition(Position, Rotation);
        doodad.SetScale(Scale);
        if (doodad.FuncGroupId != FuncGroupId)
            doodad.FuncGroupId = FuncGroupId;
    }

    public string ToDisplayString()
    {
        return $"x={Format(Position.X)}, y={Format(Position.Y)}, z={Format(Position.Z)}, " +
               $"roll={Format(Rotation.X.RadToDeg())}°, pitch={Format(Rotation.Y.RadToDeg())}°, " +
               $"yaw={Format(Rotation.Z.RadToDeg())}°, scale={Format(Scale)}, phase={FuncGroupId}";
    }

    public string ToPlacementJson(uint templateId, IReadOnlyList<uint> relatedIds)
    {
        var root = new JObject
        {
            ["UnitId"] = new JValue(templateId),
            ["Position"] = new JObject
            {
                ["X"] = new JValue(Position.X),
                ["Y"] = new JValue(Position.Y),
                ["Z"] = new JValue(Position.Z),
                ["Roll"] = new JValue(Rotation.X.RadToDeg()),
                ["Pitch"] = new JValue(Rotation.Y.RadToDeg()),
                ["Yaw"] = new JValue(Rotation.Z.RadToDeg())
            }
        };

        if (relatedIds is { Count: > 0 })
            root["RelatedIds"] = new JArray(relatedIds);
        root["Scale"] = new JValue(Scale);

        return root.ToString(Formatting.None);
    }

    private static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        if (normalized >= 180f)
            normalized -= 360f;
        if (normalized < -180f)
            normalized += 360f;
        return normalized;
    }

    private static string Format(float value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
