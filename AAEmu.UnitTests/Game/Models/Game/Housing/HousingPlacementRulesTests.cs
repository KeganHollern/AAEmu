using System.Numerics;
using System.Xml.Linq;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Xml;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingPlacementRulesTests
{
    [Test]
    public async Task Read_UsesValue1AndZoneCellOffsets()
    {
        var area = HousingAreaPolygon.Read(Geometry(), new XmlWorldZone { OriginX = 23, OriginY = 7 }).Single();
        await Assert.That(area.Id).IsEqualTo(11u);
        await Assert.That(area.Points[0]).IsEqualTo(new Vector3(24 * 1024 + 10, 9 * 1024 + 20, 30));
        await Assert.That(area.Contains(area.Points[0] + new Vector3(2, 2, 1000))).IsTrue();
        await Assert.That(area.Contains(area.Points[0] + new Vector3(-1, 2, 0))).IsFalse();
    }

    [Test]
    public async Task Read_RotationAndScale_TransformLocalPoints()
    {
        var area = HousingAreaPolygon.Read(Geometry(rotation: "0.70710678,0,0,0.70710678", scale: "2,2,1"),
            new XmlWorldZone()).Single();
        var delta = area.Points[1] - area.Points[0];
        await Assert.That(Math.Abs(delta.X)).IsLessThan(0.001f);
        await Assert.That(Math.Abs(delta.Y - 20)).IsLessThan(0.001f);
    }

    [Test]
    public async Task Read_UnboundEditorShape_PreservesTheFirstMatchMask()
    {
        var areas = HousingAreaPolygon.Read(Geometry(id: 0), new XmlWorldZone());
        await Assert.That(areas.Single().Id).IsEqualTo(0u);
    }

    [Test]
    public async Task Read_OnlyGroupOne_ParticipatesInHousingQueries()
    {
        var areas = HousingAreaPolygon.Read(Geometry().Replace("Group=\"1\"", "Group=\"2\""), new XmlWorldZone());
        await Assert.That(areas).IsEmpty();
    }

    [Test]
    public async Task ReadSources_PreservesRootOrderAndDistinctShapesWithTheSameId()
    {
        var files = new Dictionary<string, string>
        {
            ["client/na/housing_area.xml"] = Geometry(),
            ["client/cn/housing_area.xml"] = Geometry(),
            ["client/housing_area.xml"] = Geometry(id: 12).Replace("</Objects>",
                Geometry().Replace("<Objects>", string.Empty)),
            ["client/extra/housing_area.xml"] = Geometry().Replace("Pos=\"10,20,30\"", "Pos=\"30,20,30\"")
        };
        var polygons = HousingAreaPolygon.ReadSources(files.Keys, path => files[path], new XmlWorldZone());
        await Assert.That(polygons.Count).IsEqualTo(3);
        await Assert.That(polygons.Select(p => p.Id).ToArray()).IsEquivalentTo(new uint[] { 12, 11, 11 });
        await Assert.That(polygons[0].Id).IsEqualTo(12u);
        await Assert.That(polygons[1].Points[0].X).IsEqualTo(1034f);
        await Assert.That(polygons[2].Points[0].X).IsEqualTo(1054f);
    }

    [Test]
    public async Task Contains_IgnoresHeightAndUsesNativeHalfOpenBoundaries()
    {
        var area = HousingAreaPolygon.Read(Geometry(height: 50), new XmlWorldZone()).Single();
        var origin = area.Points[0];
        await Assert.That(area.Contains(origin)).IsFalse();
        await Assert.That(area.Contains(origin + new Vector3(0, 5, 0))).IsFalse();
        await Assert.That(area.Contains(origin + new Vector3(5, 0, 0))).IsFalse();
        await Assert.That(area.Contains(origin + new Vector3(10, 5, 0))).IsTrue();
        await Assert.That(area.Contains(origin + new Vector3(5, 10, 0))).IsTrue();
        await Assert.That(area.Contains(origin + new Vector3(10, 10, 0))).IsTrue();
        await Assert.That(area.Contains(origin + new Vector3(5, 5, 50))).IsTrue();
        await Assert.That(area.Contains(origin + new Vector3(5, 5, 51))).IsTrue();
        await Assert.That(area.Contains(origin + new Vector3(5, 5, -1))).IsTrue();
        await Assert.That(area.Contains(new Vector3(float.NaN, 0, 0))).IsFalse();
        await Assert.That(area.Contains2D(float.PositiveInfinity, 0)).IsFalse();
    }

    [Test]
    public async Task Contains_SlopedEdge_UsesTheStrictNativeCrossing()
    {
        var area = new HousingAreaPolygon { Points = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)] };
        await Assert.That(area.Contains2D(4, 5)).IsTrue();
        await Assert.That(area.Contains2D(5, 5)).IsFalse();
        await Assert.That(area.Contains2D(6, 5)).IsFalse();
    }

    [Test]
    public async Task Contains_ConcavePolygon_DoesNotUseOnlyBoundingBox()
    {
        var area = new HousingAreaPolygon
        {
            Points = [new(0, 0, 0), new(10, 0, 0), new(10, 3, 0), new(3, 3, 0), new(3, 10, 0), new(0, 10, 0)]
        };
        await Assert.That(area.Contains(new Vector3(2, 8, 0))).IsTrue();
        await Assert.That(area.Contains(new Vector3(8, 8, 0))).IsFalse();
    }

    [Test]
    public async Task Check_CategoryAndArea_MustMatch()
    {
        using var connection = CreateDatabase();
        var data = Load(connection);
        var world = CreateWorld();
        await Assert.That(Check(data, world, 16)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(data, world, 1)).IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
        await Assert.That(HousingPlacementRules.Check(data, world, new Vector3(-1), 16, 42, []))
            .IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
        await Assert.That(Check(data, new WorldTemplate(), 16)).IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
    }

    [Test]
    public async Task Check_Houseless_CountsEveryCharacterOnAccount()
    {
        using var connection = CreateDatabase(houseless: true);
        var data = Load(connection);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 999, 1)))
            .IsEqualTo(ErrorMessageType.HouseCannotOwnMoreHouselessCondition);
        await Assert.That(Check(data, CreateWorld(), 16, House(77, 999, 1))).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_ExistingCategory_ProhibitsItInsteadOfRequiringIt()
    {
        using var connection = CreateDatabase(existingCategory: 17);
        var data = Load(connection);
        await Assert.That(Check(data, CreateWorld(), 16)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 999, 17)))
            .IsEqualTo(ErrorMessageType.HouseCannotOwnMoreExistingCategoryCondition);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 999, 1))).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_Maximum_CountsCategoryAcrossCharacters()
    {
        using var connection = CreateDatabase(maximum: 2);
        var data = Load(connection);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 1, 16))).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 1, 16), House(42, 2, 16)))
            .IsEqualTo(ErrorMessageType.HouseCannotConstructInAreaByMaxConstructCount);
        await Assert.That(Check(data, CreateWorld(), 16, House(42, 1, 16), House(77, 2, 16), House(42, 3, 1)))
            .IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_ZeroMaximum_IsUnlimited()
    {
        using var connection = CreateDatabase();
        await Assert.That(Check(Load(connection), CreateWorld(), 16,
            Enumerable.Range(1, 10).Select(id => House(42, (uint)id, 16)).ToArray())).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_Overlap_SelectsTheFirstRegisteredAreaWithoutPriorityIntersection()
    {
        using var connection = CreateDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO housing_areas VALUES (12, 'second area', 3);
            INSERT INTO housing_groups VALUES (3, 'second group', '', 0, 'f', 0, 0, 'f');
            INSERT INTO housing_group_categories VALUES (2, 3, 7, 0);
            """;
        command.ExecuteNonQuery();
        var data = Load(connection);
        var world = CreateWorld();
        world.HousingZones[138].Add(new HousingAreaPolygon
        {
            Id = 12, Priority = 100, Points = world.HousingZones[138][0].Points
        });
        await Assert.That(Check(data, world, 16)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(data, world, 7)).IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
        world.HousingZones[138].Reverse();
        await Assert.That(Check(data, world, 7)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(data, world, 16)).IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
    }

    [Test]
    public async Task Check_UnboundFirstMatch_DoesNotFallThroughToALaterPermit()
    {
        using var connection = CreateDatabase();
        var world = CreateWorld();
        world.HousingZones[138].Insert(0, new HousingAreaPolygon
        {
            Id = 0, Points = world.HousingZones[138][0].Points
        });
        await Assert.That(Check(Load(connection), world, 16)).IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
    }

    [Test]
    public async Task Load_RetainsAllGroupFieldsAndResetsOnReload()
    {
        using var connection = CreateDatabase(houseless: true, existingCategory: 17, maximum: 3);
        var data = Load(connection);
        data.Load(connection);
        await Assert.That(data.AreaCount).IsEqualTo(1);
        await Assert.That(data.GroupCount).IsEqualTo(1);
        var group = data.GetGroup(2);
        await Assert.That(group.Houseless).IsTrue();
        await Assert.That(group.ExistingCategoryId).IsEqualTo(17u);
        await Assert.That(group.AllowedTaxDelayWeek).IsEqualTo(2);
        await Assert.That(group.CanExtend).IsTrue();
        await Assert.That(group.CategoryLimits[16]).IsEqualTo(3u);
    }

    [Test]
    public async Task RealClient_CompactAndGeometry_Agree()
    {
        var compactPath = Environment.GetEnvironmentVariable("AAEMU_HOUSING_COMPACT");
        var clientRoot = Environment.GetEnvironmentVariable("AAEMU_HOUSING_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(compactPath) && !string.IsNullOrEmpty(clientRoot),
            "Set AAEMU_HOUSING_COMPACT and AAEMU_HOUSING_CLIENT_ROOT for the exact-client data check.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compactPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = Load(connection);
        await Assert.That(data.AreaCount).IsEqualTo(401);
        await Assert.That(data.GroupCount).IsEqualTo(15);
        await Assert.That(data.GetGroup(13).Houseless).IsTrue();
        await Assert.That(data.GetGroup(9).ExistingCategoryId).IsEqualTo(17u);
        var worldRoot = Path.Combine(clientRoot, "game", "worlds", "main_world");
        var zones = XDocument.Load(Path.Combine(worldRoot, "world.xml")).Root.Element("ZoneList").Elements("Zone")
            .ToDictionary(e => (uint)e.Attribute("id"), e => new XmlWorldZone
            {
                OriginX = (int)e.Attribute("originX"),
                OriginY = (int)e.Attribute("originY")
            });
        var polygons = new List<HousingAreaPolygon>();
        foreach (var (id, zone) in zones)
        {
            var folder = Path.Combine(worldRoot, "level_design", "zone", id.ToString(), "client");
            if (!Directory.Exists(folder))
                continue;
            polygons.AddRange(HousingAreaPolygon.ReadSources(
                Directory.GetFiles(folder, "housing_area.xml", SearchOption.AllDirectories), File.ReadAllText, zone));
        }
        await Assert.That(polygons.Count).IsGreaterThan(300);
        foreach (var polygon in polygons.Where(p => p.Id != 0))
            await Assert.That(data.GetArea(polygon.Id)).IsNotNull();
        var first = polygons.First(p => p.Id == 11);
        await Assert.That(first.Points[0].X).IsEqualTo(24 * 1024 + 654.65906f);
        await Assert.That(first.Points[0].Y).IsEqualTo(9 * 1024 + 675.46289f);
        var overlapPosition = new Vector3(20300, 18200, 0);
        var overlapFolder = Path.Combine(worldRoot, "level_design", "zone", "283", "client");
        foreach (var path in Directory.GetFiles(overlapFolder, "housing_area.xml", SearchOption.AllDirectories))
        {
            var overlapPolygons = HousingAreaPolygon.Read(File.ReadAllText(path), zones[283]).ToList();
            var matches = overlapPolygons.Where(p => p.Contains(overlapPosition)).ToArray();
            await Assert.That(matches.Select(p => p.Id).Contains(208u)).IsTrue();
            await Assert.That(matches.Select(p => p.Id).Contains(209u)).IsTrue();
            await Assert.That(matches[0].Id).IsEqualTo(208u);
            var world = new WorldTemplate { HousingZones = new() { [283] = overlapPolygons } };
            await Assert.That(HousingPlacementRules.Check(data, world, overlapPosition, 1, 42, []))
                .IsEqualTo(ErrorMessageType.NoErrorMessage);
            await Assert.That(HousingPlacementRules.Check(data, world, overlapPosition, 7, 42, []))
                .IsEqualTo(ErrorMessageType.HouseCannotLoacateInvalidCategoryArea);
        }
        var mergedOverlap = HousingAreaPolygon.ReadSources(
            Directory.GetFiles(overlapFolder, "housing_area.xml", SearchOption.AllDirectories).Reverse(), File.ReadAllText, zones[283]);
        await Assert.That(mergedOverlap.Count).IsEqualTo(6);
        await Assert.That(mergedOverlap.First(p => p.Contains(overlapPosition)).Id).IsEqualTo(208u);
        Console.WriteLine($"Housing client check: {polygons.Count} polygons, {polygons.Select(p => p.Id).Distinct().Count()} area IDs.");
    }

    private static string Geometry(uint id = 11, int height = 0, string rotation = "1,0,0,0", string scale = "1,1,1") => $$"""
        <Objects><Entity Name="test" Pos="10,20,30" cellX="1" cellY="2" Rotate="{{rotation}}" Scale="{{scale}}">
        <Area Id="777" value1="{{id}}" Group="1" Priority="0" Height="{{height}}"><Points>
        <Point Pos="0,0,0"/><Point Pos="10,0,0"/><Point Pos="10,10,0"/><Point Pos="0,10,0"/>
        </Points></Area></Entity></Objects>
        """;

    private static SqliteConnection CreateDatabase(bool houseless = false, uint existingCategory = 0, uint maximum = 0)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE housing_areas (id INTEGER, name TEXT, housing_group_id INTEGER);
            CREATE TABLE housing_groups (id INTEGER, name TEXT, desc TEXT, doodad_id INTEGER,
                houseless TEXT, existing_category_id INTEGER, allowed_tax_delay_week INTEGER, can_extend TEXT);
            CREATE TABLE housing_group_categories (id INTEGER, housing_group_id INTEGER, category_id INTEGER, max_construct_count INTEGER);
            INSERT INTO housing_areas VALUES (11, 'test area', 2);
            INSERT INTO housing_groups VALUES (2, 'test group', 'test description', 100, @houseless, @existing, 2, 't');
            INSERT INTO housing_group_categories VALUES (1, 2, 16, @maximum);
            """;
        command.Parameters.AddWithValue("@houseless", houseless ? "t" : "f");
        command.Parameters.AddWithValue("@existing", existingCategory);
        command.Parameters.AddWithValue("@maximum", maximum);
        command.ExecuteNonQuery();
        return connection;
    }

    private static HousingAreaGameData Load(SqliteConnection connection)
    {
        var data = new HousingAreaGameData();
        data.Load(connection);
        data.PostLoad();
        return data;
    }

    private static WorldTemplate CreateWorld() => new()
    {
        HousingZones = new()
        {
            [138] = [new HousingAreaPolygon
        {
            Id = 11, Points = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)]
        }]
        }
    };

    private static House House(uint account, uint owner, uint category) => new()
    {
        AccountId = account,
        OwnerId = owner,
        Template = new HousingTemplate { CategoryId = category }
    };

    private static ErrorMessageType Check(HousingAreaGameData data, WorldTemplate world, uint category, params House[] houses) =>
        HousingPlacementRules.Check(data, world, new Vector3(5, 5, 0), category, 42, houses);
}
