using System.Collections.Frozen;
using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Models.Game.Quests;

internal readonly record struct QuestInteractionSphere(Vector3 Center, float Radius);

internal static class QuestDoodadInteractionShapes
{
    private static readonly Lazy<FrozenDictionary<string, QuestInteractionSphere[]>> Models = new(Load);

    internal static IReadOnlyList<QuestInteractionSphere> Get(Doodad doodad)
    {
        var phase = doodad.Template?.FuncGroups.FirstOrDefault(group => group.Id == doodad.FuncGroupId);
        var model = string.IsNullOrEmpty(phase?.Model) ? doodad.Template?.Model : phase.Model;
        return string.IsNullOrEmpty(model) ? null : Models.Value.GetValueOrDefault(Normalize(model));
    }

    private static string Normalize(string model)
    {
        var normalized = model.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        if (normalized.StartsWith("cgf://", StringComparison.Ordinal) &&
            !normalized.StartsWith("cgf://game/", StringComparison.Ordinal))
            return "cgf://game/" + normalized[6..];
        return normalized;
    }

    private static FrozenDictionary<string, QuestInteractionSphere[]> Load()
    {
        using var stream = typeof(QuestDoodadInteractionShapes).Assembly
            .GetManifestResourceStream("AAEmu.Game.QuestDoodadInteractionShapes.json")
            ?? throw new InvalidOperationException("The r208022 quest doodad interaction shapes are missing.");
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, QuestInteractionSphere[]>();
        foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
        {
            var spheres = model.GetProperty("spheres").EnumerateArray().Select(sphere =>
            {
                var center = sphere.GetProperty("center");
                return new QuestInteractionSphere(new Vector3(center[0].GetSingle(), center[1].GetSingle(), center[2].GetSingle()),
                    sphere.GetProperty("radius").GetSingle());
            }).ToArray();
            result.Add(Normalize(model.GetProperty("model").GetString()!), spheres);
        }
        return result.ToFrozenDictionary();
    }
}
