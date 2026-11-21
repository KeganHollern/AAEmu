using System.Numerics;
using System.Reflection;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingGeometryAssetsTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExactClient_PlacedPhase_UsesLowestAnimationIdForCgaAndPrefab(bool prefab)
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 phase clock check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        var manager = new DoodadManager(null, null, null, null, null);
        var singleton = typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = singleton.GetValue(null);
        var name = prefab ? "operate" : "opened";
        typeof(DoodadManager).GetField("_phaseFuncTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
            {
                [nameof(DoodadFuncAnimate)] = new()
                {
                    [7] = new DoodadFuncAnimate { Id = 7, Name = name, PlayOnce = true },
                    [8] = new DoodadFuncAnimate { Id = 8, Name = "missing_clip", PlayOnce = false }
                }
            });
        singleton.SetValue(null, manager);
        try
        {
            const string character = "objects/env/01_nuia/001_ndeco/making/ndeco_making_sewing01.chr";
            var xml = Encoding.UTF8.GetBytes($"<PrefabsLibrary><Prefab Name='sewing'><Objects><Object Type='Entity' EntityClass='AnimObject' Pos='10,20,30'><Properties object_Model='{character}'><Animation bPlaying='0'/></Properties></Object></Objects></Prefab></PrefabsLibrary>");
            Stream Open(string file) => file == "game/prefabs/test.xml" ? new MemoryStream(xml) : source.FileExists(file) ? source.GetFileStream(file) : null;
            var uri = prefab ? "prefab://prefabs/test.xml/sewing" : "cga://objects/env/01_nuia/001_ndeco/housing/ndeco_housing_bookshelf01.cga";
            var assets = new HousingGeometryAssets(Open, (_, _) => []);
            var utcNow = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
            var doodad = new Doodad { Template = new DoodadTemplate { Model = uri }, PhaseTime = utcNow.AddSeconds(-0.5) };
            doodad.CurrentPhaseFuncs.Add(new DoodadPhaseFunc { FuncId = 8, FuncType = nameof(DoodadFuncAnimate) });
            doodad.CurrentPhaseFuncs.Add(new DoodadPhaseFunc { FuncId = 7, FuncType = nameof(DoodadFuncAnimate) });
            var expected = new CryGeometryResolver(Open).LoadAnimationPose(uri, name, 0.5, false);
            var actual = assets.LoadDoodad(doodad, utcNow);
            await Assert.That(actual.Parts.Count).IsGreaterThan(0);
            await Assert.That(actual.Bounds).IsEqualTo(expected.Bounds);
            await Assert.That(actual.Parts.Select(part => part.Transform).SequenceEqual(expected.Parts.Select(part => part.Transform))).IsTrue();
        }
        finally
        {
            singleton.SetValue(null, previous);
            source.Close();
        }
    }

    [Test]
    public async Task LoadModel_OnlyEffectsAndMissingAnimation_HasNoPlacementBounds()
    {
        var assets = new HousingGeometryAssets(path => path.EndsWith(".xml", StringComparison.Ordinal)
            ? new MemoryStream(Encoding.UTF8.GetBytes("<PrefabsLibrary><Prefab Name='effect'><Objects><Object Type='Decal'/></Objects></Prefab></PrefabsLibrary>"))
            : null, (_, _) => ["prefab://prefabs/fx.xml/effect", "cga://objects/missing.cga"]);
        var model = assets.LoadModel(1);
        await Assert.That(model.HasModelBounds).IsFalse();
        await Assert.That(model.Parts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CollisionBounds_ProxyOutsideModel_KeepsBothWorldVolumes()
    {
        var model = new CryGeometryAsset(new CryBounds(-Vector3.One, Vector3.One),
            [new CryGeometryPart(new CrySphere(Vector3.Zero, 2), Matrix4x4.CreateTranslation(5, 0, 0), 0x1000, "", "bone")]);
        var bounds = HousingGeometryAssets.CollisionBounds(model, Matrix4x4.CreateTranslation(100, 200, 300));
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(99, 198, 298));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(107, 202, 302));
    }

    [Test]
    public async Task ExactClient_PreviewAndPlacedModels_UseBindAndPersistedClocks()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 runtime pose check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            const string uri = "cga_loop://objects/env/02_harihara/001_housing/housing_castle/hari_gate_castledoor.cga";
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            var resolver = new CryGeometryResolver(Open);
            var assets = new HousingGeometryAssets(Open, (_, _) => [uri]);
            var utcNow = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
            var template = new HousingTemplate { MainModelId = 1 };
            var house = new House { Template = template, PlaceDate = utcNow.AddSeconds(-0.5) };
            var doodad = new Doodad { Template = new DoodadTemplate { Model = uri }, PhaseTime = house.PlaceDate };
            var expected = resolver.LoadPose(uri, 0.5);
            await Assert.That(expected.Parts.Count).IsGreaterThan(0);
            await Assert.That(assets.LoadHouse(template).Bounds).IsEqualTo(resolver.Load(uri).Bounds);
            await Assert.That(assets.LoadDoodad(doodad.Template).Bounds).IsEqualTo(resolver.Load(uri).Bounds);
            await Assert.That(assets.LoadHouse(house, utcNow).Bounds).IsEqualTo(expected.Bounds);
            await Assert.That(assets.LoadDoodad(doodad, utcNow).Parts.Select(part => part.Transform)
                .SequenceEqual(expected.Parts.Select(part => part.Transform))).IsTrue();
            house.PlaceDate = utcNow.AddDays(1);
            await Assert.That(assets.LoadHouse(house, utcNow).Bounds).IsEqualTo(resolver.LoadPose(uri, 0).Bounds);
        }
        finally { source.Close(); }
    }

    [Test]
    public async Task LoadHouse_AutoZOffsetsModelGeometry()
    {
        const string xml = """
            <PrefabsLibrary><Prefab Name="house"><Objects>
              <Object Type="Comment" Name="helper" Pos="2,3,12"/>
              <Object Type="WaterVolume" Pos="0,0,12" VolumeDepth="100"><Points>
                <Point Pos="0,0,0"/><Point Pos="4,0,0"/><Point Pos="4,4,0"/><Point Pos="0,4,0"/>
              </Points></Object>
            </Objects></Prefab></PrefabsLibrary>
            """;
        var assets = new HousingGeometryAssets(path => path == "game/prefabs/house.xml"
            ? new MemoryStream(Encoding.UTF8.GetBytes(xml)) : null, (_, _) => ["prefab://prefabs/house.xml/house"]);
        var raw = assets.LoadModel(1);
        var offset = new Vector3(2, 4, 1.5f);
        HousingTemplate Template(bool autoZ) => new()
        {
            MainModelId = 1, AutoZ = autoZ,
            AutoZOffsetX = offset.X, AutoZOffsetY = offset.Y, AutoZOffsetZ = offset.Z
        };

        var shifted = assets.LoadHouse(Template(true));
        await Assert.That(shifted.Bounds.Min).IsEqualTo(raw.Bounds.Min - offset);
        await Assert.That(shifted.Bounds.Max).IsEqualTo(raw.Bounds.Max - offset);
        await Assert.That(shifted.Helpers.Single().Transform.Translation)
            .IsEqualTo(raw.Helpers.Single().Transform.Translation - offset);
        await Assert.That(assets.LoadHouse(Template(false)).Bounds).IsEqualTo(raw.Bounds);
        await Assert.That(assets.LoadModel(1).Bounds).IsEqualTo(raw.Bounds);
    }

    [Test]
    public async Task ExactClient_CottageInterior_UsesTheAutoZModelOffset()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 cottage placement check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            const string prefix = "prefab://prefabs/housing_big.xml/nuia_housing_small.nuia_small_";
            var assets = new HousingGeometryAssets(Open, (_, _) => [prefix + "floor01", prefix + "wall01", prefix + "roof01"]);
            var template = new HousingTemplate
            {
                Id = 172, MainModelId = 1116, AutoZ = true, AutoZOffsetZ = 1,
                GardenRadius = 7.5f, Alley = 1, ExtraHeightAbove = 10, ExtraHeightBelow = 10
            };
            var house = new House { Template = template };
            house.SetInitialConstructionStep();
            var logicalPosition = new Vector3(12476, 15692, 164.136f);
            house.Transform.Local.SetPosition(logicalPosition, new Vector3(0, 0, 0.346317f));
            var logical = HousingGeometryAssets.Transform(house);
            var raw = assets.LoadModel(template.MainModelId);
            var placed = assets.LoadHouse(house, DateTime.UnixEpoch);
            await Assert.That(HousingGeometryAssets.Transform(house).Translation).IsEqualTo(logicalPosition);

            using var heightmap = Open("game/worlds/main_world/cells/012_015/client/terrain/heightmap.dat");
            var terrain = CryTerrainGrid.Read(heightmap, new Vector2(12288, 15360));
            var rawScene = new CryGeometryScene(_ => [new CryGeometryInstance(0, raw, logical)], _ => true);
            var instances = new List<CryGeometryInstance> { new(0, placed, logical) };
            var scene = new CryGeometryScene(_ => instances, _ => true);
            var houseBounds = HousingGeometryAssets.HouseBounds(placed.Bounds, logical);
            var garden = HousingGeometryAssets.GardenBounds(template, placed.Bounds, logical);
            var rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(0, 0, -0.98504543f, -0.17229466f));
            const string table = "prefab://prefabs/ndeco_valentine.xml/ndeco_valentine.ndeco_valentine_table_off";
            const string chest = "prefab://prefabs/itempart.xml/craft.hs_f_safe_a";
            (string Model, Vector3 Local)[] placements =
            [
                (table, new(-1.05499f, 1.8077122f, 0.6606293f)),
                (table, new(-1.3619859f, 0.58440614f, 0.6606293f)),
                (chest, new(-0.63212806f, -0.39377248f, 0.6606293f))
            ];
            HousingDecorationRayResult Trace(Vector3 from, Vector3 vector)
            {
                var length = vector.Length();
                var direction = vector / length;
                var collision = scene.Raycast(from, direction, length, out var hit);
                if (collision == CryIntersection.Indeterminate)
                    return new HousingDecorationRayResult(collision, false);
                var terrainHit = terrain.Raycast(from, direction,
                    collision == CryIntersection.Intersects ? hit.Hit.Distance : length, out _);
                return terrainHit == CryIntersection.Clear
                    ? new HousingDecorationRayResult(collision, false)
                    : new HousingDecorationRayResult(terrainHit, true);
            }

            foreach (var (model, local) in placements)
            {
                var transform = rotation * Matrix4x4.CreateTranslation(local) * logical;
                var pivot = transform.Translation;
                var start = pivot + Vector3.UnitZ * 0.01f;
                var direction = -Vector3.UnitZ;
                await Assert.That(rawScene.Raycast(start, direction, 0.02f, out _)).IsEqualTo(CryIntersection.Clear);
                await Assert.That(scene.Raycast(start, direction, 0.02f, out var hit)).IsEqualTo(CryIntersection.Intersects);
                await Assert.That(Vector3.DistanceSquared(hit.Hit.Position, pivot) <= 0.01f * 0.01f).IsTrue();
                await Assert.That(hit.Hit.Normal.Z >= 0.999f).IsTrue();

                var item = assets.Load(model);
                var design = new HousingDecoration { AllowOnFloor = true, AllowPivotOnGarden = model == table };
                ErrorMessageType Check(Matrix4x4 pose) => HousingDecorationGeometry.Check(design, item.Bounds, pose,
                    houseBounds, garden, true, hit.Hit.Normal, true, Trace, box => scene.IntersectBox(box));
                await Assert.That(Check(transform)).IsEqualTo(ErrorMessageType.NoErrorMessage);
                instances.Add(new CryGeometryInstance(1, item, transform));
                await Assert.That(Check(transform)).IsEqualTo(ErrorMessageType.HouseCannotDecorateOverlap);
                instances.RemoveAt(instances.Count - 1);

                transform.Translation = new Vector3(12484, 15692, pivot.Z);
                await Assert.That(Check(transform)).IsEqualTo(ErrorMessageType.HouseCannotDecorateOutOfGarden);
            }
        }
        finally { source.Close(); }
    }

    [Test]
    public async Task GardenBounds_ReservesAlleyAndUsesRotatedModelHeight()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1, ExtraHeightAbove = 2, ExtraHeightBelow = 3 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var transform = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateTranslation(100, 100, 10);
        var bounds = HousingGeometryAssets.GardenBounds(template, local, transform);
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(93, 93, 6));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(107, 107, 19));
    }

    [Test]
    public async Task GardenBounds_ZeroRadiusUsesAuthoredBounds()
    {
        var template = new HousingTemplate { GardenRadius = 0 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var bounds = HousingGeometryAssets.GardenBounds(template, local, Matrix4x4.CreateTranslation(100, 100, 10));
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(94, 96, 9));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(106, 104, 17));
        await Assert.That(HousingDecorationGeometry.IsWithinSelectionRange(bounds, new Vector3(100, 100, 100))).IsFalse();
    }

    [Test]
    public async Task GardenBounds_FullPlotDoesNotReserveAlley()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1 };
        var bounds = HousingGeometryAssets.GardenBounds(template, new CryBounds(Vector3.Zero, Vector3.One),
            Matrix4x4.CreateTranslation(100, 100, 0), false);
        await Assert.That(bounds.Min.X).IsEqualTo(92f);
        await Assert.That(bounds.Max.X).IsEqualTo(108f);
    }
}
