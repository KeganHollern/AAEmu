using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using CgfConverter.Structs;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    public enum CrossingObstacle { None, Wall, Roof, LowRoof, UnresolvedRoof, UnresolvedBody, DiagonalCorner, DiagonalBody, MissingStance }

    [Test]
    [Arguments(-0.008f, 0f, CrossingObstacle.None, 1, DeckZ)]
    [Arguments(-0.008f, 0f, CrossingObstacle.None, -1, DeckZ)]
    [Arguments(0f, 0f, CrossingObstacle.None, 1, DeckZ)]
    [Arguments(0.0025f, 0f, CrossingObstacle.None, 1, 0f)]
    [Arguments(0.0025f, 0f, CrossingObstacle.None, -1, 0f)]
    [Arguments(-0.008f, 0.004f, CrossingObstacle.None, 1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.Wall, 1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.Wall, -1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.Roof, 1, DeckZ)]
    [Arguments(-0.008f, 0f, CrossingObstacle.UnresolvedRoof, 1, DeckZ)]
    [Arguments(-0.008f, 0f, CrossingObstacle.LowRoof, 1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.UnresolvedBody, 1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.MissingStance, 1, 0f)]
    [Arguments(-0.008f, 0f, CrossingObstacle.DiagonalCorner, 1, DeckZ)]
    [Arguments(-0.008f, 0f, CrossingObstacle.DiagonalBody, 1, 0f)]
    public async Task MoveTowards_SeparatePlacedFloorsCheckTheWholeStep(float gap, float heightDifference,
        CrossingObstacle obstacle, int direction, float expectedZ)
    {
        var npc = CreateDeckWalker();
        // Stock Pig 2582 uses model 329, actor 136 and a capsule 0.5 m wide on either side.
        var stance = new GameStance { HeightCollider = 0.7f, Size = new Vector3(0.5f, 0.5f, 0.2f), UseCapsule = true };
        var actor = new ActorModel { Stances = { [GameStanceType.Combat] = stance, [GameStanceType.Relaxed] = stance } };
        if (obstacle == CrossingObstacle.MissingStance)
            actor.Stances.Clear();
        var models = new ModelManager();
        SetPrivateMember(models, "_modelTypes", new Dictionary<uint, ModelType> { [329] = new() { SubType = "ActorModel", SubId = 136 } });
        SetPrivateMember(models, "_models", new Dictionary<string, Dictionary<uint, Model>> { ["ActorModel"] = new() { [136] = actor } });
        UseMovementService(models);
        npc.ModelId = 329;
        npc.Template = new NpcTemplate { Id = 2582, ModelId = 329, Scale = 1 };
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        const float seamX = 110.75f;
        const float y = 100f;
        var mesh = new CryTriangleMesh([new(-1, -1, 0), new(0, -1, 0), new(0, 1, 0), new(-1, 1, 0)],
            [0, 1, 2, 0, 2, 3], []);
        var floor = new CryGeometryAsset(new CryBounds(new(-1, -1, 0), new(0, 1, 0)),
            [new CryGeometryPart(mesh, Matrix4x4.Identity, 0x1000, "", "floor")]);
        var wall = new CryGeometryAsset(new CryBounds(new(0, -1, 0), new(0, 1, 2)),
            [new CryGeometryPart(new CryTriangleMesh([new(0, -1, 0), new(0, 1, 0), new(0, 0, 2)], [0, 1, 2], []),
                Matrix4x4.Identity, 0x1000, "", "wall")]);
        var obstacleShape = new CryBox(Vector3.Zero, new Vector3(0.02f, 0.02f, 0.1f), Matrix4x4.Identity);
        var smallObstacle = new CryGeometryAsset(new CryBounds(-obstacleShape.HalfSize, obstacleShape.HalfSize),
            [new CryGeometryPart(obstacleShape, Matrix4x4.Identity, 0x1000, "", "obstacle")]);
        world.GeoData = new AiGeoDataManager(world) { GroundBrushGeometry = b => b.ModelUri switch
        {
            "wall.cgf" => wall, "block.cgf" => smallObstacle, "missing.cgf" => throw new FileNotFoundException(), _ => floor
        } };
        var objects = world.Cells[0, 0].LoadedObjectDat;
        objects.AssetPathsList = [new AssetPath { Name = "deck.cgf" }, new AssetPath { Name = "wall.cgf" },
            new AssetPath { Name = "block.cgf" }, new AssetPath { Name = "missing.cgf" }];
        objects.PrefabsList = [new ObjectDataType1Brush
        {
            StartPos = new(seamX - 1, y - 1, DeckZ), EndPos = new(seamX, y + 1, DeckZ),
            Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1, M14 = seamX, M24 = y, M34 = DeckZ }
        }, new ObjectDataType1Brush
        {
            // Reuse the same mesh with a distinct rotation, nonuniform scale and translation.
            StartPos = new(seamX + gap, y - 1, DeckZ + heightDifference),
            EndPos = new(seamX + gap + 2, y + 1, DeckZ + heightDifference),
            Matrix3X4 = new Matrix3x4 { M11 = -2, M22 = -1, M33 = 1,
                M14 = seamX + gap, M24 = y, M34 = DeckZ + heightDifference }
        }];
        if (obstacle == CrossingObstacle.Wall)
            objects.PrefabsList.Add(new ObjectDataType1Brush
            {
                PathId = 1, StartPos = new(seamX, y - 1, DeckZ), EndPos = new(seamX, y + 1, DeckZ + 2),
                Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1, M14 = seamX, M24 = y, M34 = DeckZ }
            });

        if (obstacle is CrossingObstacle.Roof or CrossingObstacle.UnresolvedRoof or CrossingObstacle.LowRoof or CrossingObstacle.UnresolvedBody)
        {
            var roofZ = DeckZ + (obstacle is CrossingObstacle.LowRoof or CrossingObstacle.UnresolvedBody ? 1 : 15);
            objects.PrefabsList.Add(new ObjectDataType1Brush
            {
                PathId = obstacle is CrossingObstacle.Roof or CrossingObstacle.LowRoof ? 0 : 3,
                StartPos = new(seamX - 0.5f, y - 1, roofZ), EndPos = new(seamX + 0.5f, y + 1, roofZ),
                Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1, M14 = seamX + 0.5f, M24 = y, M34 = roofZ }
            });
        }
        var delta = Vector3.UnitX * direction * Step;
        if (obstacle is CrossingObstacle.DiagonalCorner or CrossingObstacle.DiagonalBody)
        {
            delta = new Vector3(1.5f, 1.5f, 0);
            var offset = obstacle == CrossingObstacle.DiagonalCorner ? 0.65f : 0.2f;
            var center = new Vector3(seamX - offset, y + offset, DeckZ + 0.5f);
            objects.PrefabsList.Add(new ObjectDataType1Brush
            {
                PathId = 2, StartPos = center - obstacleShape.HalfSize, EndPos = center + obstacleShape.HalfSize,
                Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1, M14 = center.X, M24 = center.Y, M34 = center.Z }
            });
        }
        var start = new Vector3(seamX, y, DeckZ) - delta / 2;
        npc.Transform.Local.SetPosition(start);
        npc.MoveTowards(start + delta * 2, delta.Length(), rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo((start + delta) with { Z = expectedZ });
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expectedZ);
    }
}
