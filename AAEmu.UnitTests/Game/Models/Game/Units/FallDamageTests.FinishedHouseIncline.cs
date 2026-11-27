using System.Numerics;

using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    public async Task FinishedFellowshipPlaza_PlayerLandsOnBothFacesOfItsTerrace()
    {
        const string prefab = "prefab://prefabs/housing_big.xml/housing_comhouse.housing_comhouse";
        await WithFinishedHouseWorld((id, _) => id == 1443 ? [prefab] : [], async world =>
        {
            var plaza = new House
            {
                Id = 1, OwnerId = 1, ObjId = 268, ParentWorld = world,
                Template = new HousingTemplate { Id = 278, MainModelId = 1443, AutoZ = true, AutoZOffsetZ = 1.5f }
            };
            plaza.SetInitialConstructionStep();
            plaza.Transform.Local.SetPosition(new(12532, 15664, 164.126342773f), new(0, 0, 0.0989478f));
            var region = world.GetRegionByPos(plaza.Transform.World.Position);
            region.AddObject(plaza);
            var placement = HousingGeometryAssets.Transform(plaza);
            Vector3 WorldPosition(float x, float y, float z) => Vector3.Transform(new Vector3(x, y, z), placement);
            var ground = world.Template.GeoData;

            // Signed housing_comhouse.cgf. Triangle 23 rises 5.54 mm across the terrace.
            // Triangle 22 is level at world precision. The two feet are 1.25 m apart.
            var slope = WorldPosition(-7, -7.75f, 1.8548167f);
            var level = WorldPosition(-7.75f, -6.75f, 1.854171f);
            foreach (var foot in new[] { level, slope })
            {
                var player = new InclinePlayer { ParentWorld = world, Region = region, Hp = 9516 };
                void Observe(float offset, double time)
                {
                    player.Transform.Local.SetPosition(foot + Vector3.UnitZ * offset);
                    player.ObserveFallMovement(false, time);
                }

                Observe(0, 0);
                Observe(0.975f, 0.16);
                Observe(1.3f, 0.32);
                Observe(0.975f, 0.48);
                await Assert.That(player.Impacts).IsEmpty();
                Observe(0, 0.64);
                Observe(0, 0.8);
                await Assert.That(player.Impacts.Count).IsEqualTo(1);
                await Assert.That(ground.HasFinishedHouseFallSupport(player, foot + Vector3.UnitZ * 0.01f)).IsFalse();
                await Assert.That(ground.HasFinishedHouseFallSupport(player, foot - Vector3.UnitZ * 0.01f)).IsFalse();
            }

            var observer = new InclinePlayer { ParentWorld = world, Region = region };
            // The outer raised rim ends between these two points.
            await Assert.That(ground.HasFinishedHouseFallSupport(observer, WorldPosition(-8.3f, -8.8f, 2.1080072f))).IsTrue();
            await Assert.That(ground.HasFinishedHouseFallSupport(observer, WorldPosition(-8.4f, -8.9f, 2.1080072f))).IsFalse();
            // Face 35 has normal Z 0.12819, outside the stock player's fall angle.
            await Assert.That(ground.HasFinishedHouseFallSupport(observer, WorldPosition(-7.083117f, -1.9525084f, 2.0252428f))).IsFalse();
            var otherUnit = new FallUnit { ParentWorld = world, Region = region };
            await Assert.That(ground.HasFinishedHouseFallSupport(otherUnit, level)).IsTrue();
            await Assert.That(ground.HasFinishedHouseFallSupport(otherUnit, slope)).IsFalse();
            region.RemoveObject(plaza);
            await Assert.That(ground.HasFinishedHouseFallSupport(observer, slope)).IsFalse();
        });
    }
}
