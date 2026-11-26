using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

public sealed class QuestEscortTerrainTests
{
    [Test]
    [Arguments("AIPath_141_clay02_580", 17052.350f, 7723.440f, 129.549f, 17019f, 7742f, 135.836f)]
    [Arguments("q1313_anci", 22373.195f, 12181.120f, 258.004f, 22337.443f, 12154.510f, 266.515f)]
    public async Task StockEscort_StaysOnMeasuredTerrainWithRoomForTheGuard(
        string route, float startX, float startY, float startZ, float endX, float endY, float endZ)
    {
        var archive = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(archive), "Set AAEMU_HOUSING_GAME_PAK to check the stock escort paths.");
        var points = new AiPathsManager().LoadAiPathPoints(route).Select(point => point.Position).ToList();
        await Assert.That(points.Count).IsGreaterThan(1);
        await Assert.That(points[0]).IsEqualTo(new Vector3(startX, startY, startZ));
        await Assert.That(points[^1]).IsEqualTo(new Vector3(endX, endY, endZ));

        var source = new ClientSource { PathName = archive, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var cellX = (int)startX / WorldManager.CELL_SIZE;
            var cellY = (int)startY / WorldManager.CELL_SIZE;
            var cells = "game/worlds/main_world/cells/";
            var cell = $"{cells}{cellX:000}_{cellY:000}/";
            var assets = new HousingGeometryAssets(path =>
            {
                if (path.StartsWith(cells, StringComparison.Ordinal) && !path.StartsWith(cell, StringComparison.Ordinal))
                    return null;
                return source.FileExists(path) ? source.GetFileStream(path) : null;
            }, (_, _) => []);
            var world = WorldTemplateTests.CreateWorldTemplate(cellX + 1, cellY + 1);
            world.Name = "main_world";
            var terrain = assets.GetTerrain(world, cellX, cellY);
            var objects = assets.GetWorld(world);
            var failures = new List<string>();
            var bodyHeights = new[] { 0.2f, 0.7f, 1.2f, 1.8f };
            for (var segment = 1; segment < points.Count; segment++)
            {
                var from = points[segment - 1];
                var to = points[segment];
                var side = Vector2.Normalize(new Vector2(from.Y - to.Y, to.X - from.X));
                var margin = new Vector3(1, 1, 3);
                var parts = objects.Query(Vector3.Min(from, to) - margin, Vector3.Max(from, to) + margin)
                    .SelectMany(obj => assets.LoadStatic(obj).Parts
                        .Where(part => part.PhysicsType == CryGeometryLayerRules.Solid)
                        .Select(part => (Part: part, obj.Transform, Bounds: CryGeometryQueries.GetBounds(part, obj.Transform))))
                    .ToList();
                bool HitsSolid(Vector3 a, Vector3 b)
                {
                    var ray = b - a;
                    var length = ray.Length();
                    var bounds = new CryBounds(Vector3.Min(a, b), Vector3.Max(a, b));
                    return parts.Any(p => p.Bounds.Intersects(bounds) &&
                        (CryGeometryQueries.Raycast(p.Part, p.Transform, a, ray / length, length, true, out _) ||
                         CryGeometryQueries.Raycast(p.Part, p.Transform, b, -ray / length, length, true, out _)));
                }

                // Sample both sides of the guard and the center, at most 20 cm apart.
                var samples = (int)Math.Ceiling(Vector3.Distance(from, to) / 0.2f);
                foreach (var offset in new[] { -0.35f, 0f, 0.35f })
                {
                    var lastFoot = Vector3.Zero;
                    for (var sample = 0; sample <= samples; sample++)
                    {
                        var path = Vector3.Lerp(from, to, (float)sample / samples) + new Vector3(side * offset, 0);
                        var z = terrain.SampleHeight(path.X, path.Y);
                        var foot = new Vector3(path.X, path.Y, z);
                        if (!float.IsFinite(z) || terrain.IsHole(path.X, path.Y) || Math.Abs(z - path.Z) > 0.75f)
                            failures.Add($"{route} leaves the stock floor at {path}");
                        else if (HitsSolid(foot + Vector3.UnitZ * 0.2f, foot + Vector3.UnitZ * 1.8f))
                            failures.Add($"{route} crosses a solid at {foot}");
                        else if (sample > 0 && bodyHeights.Any(height =>
                                     HitsSolid(lastFoot + Vector3.UnitZ * height, foot + Vector3.UnitZ * height)))
                            failures.Add($"{route} crosses a solid between {lastFoot} and {foot}");
                        lastFoot = foot;
                    }
                }
            }
            await Assert.That(failures).IsEmpty();
        }
        finally
        {
            source.Close();
        }
    }
}
