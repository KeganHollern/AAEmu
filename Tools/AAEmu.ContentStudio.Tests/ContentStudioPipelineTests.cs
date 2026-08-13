using AAEmu.ContentStudio.Core;
using AAEmu.ContentStudio.Core.Models;
using AAEmu.ContentStudio.Core.Services;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Tests;

public class ContentStudioPipelineTests
{
    [Test]
    public async Task Build_CompilesRecipeAndWorkbenchGraph()
    {
        using var workspace = TestWorkspace.Create();
        var result = new BuildService().Build(workspace.CreateBuildRequest());

        var catalog = new CompactCatalogService();
        var recipe = catalog.GetRecipe(result.ArtifactPath, 9_100_000);
        var workbench = catalog.GetWorkbench(result.ArtifactPath, 9_200_000);

        await Assert.That(recipe).IsNotNull();
        await Assert.That(recipe!.RequiredDoodadId).IsEqualTo(9_200_000u);
        await Assert.That(recipe.CraftPackIds).Contains(9_300_000u);
        await Assert.That(recipe.Materials).HasSingleItem();
        await Assert.That(workbench).IsNotNull();
        await Assert.That(workbench!.RecipeIds).Contains(9_100_000u);
        await Assert.That(result.Manifest.Validation.IsValid).IsTrue();
    }

    [Test]
    public async Task BaselineVerifier_RejectsChangedDatabase()
    {
        using var workspace = TestWorkspace.Create();
        File.AppendAllText(workspace.BaselinePath, "changed");

        var descriptor = new ProjectRepository().LoadBaseline(workspace.DescriptorPath);
        var report = new BaselineVerifier().Verify(workspace.BaselinePath, descriptor);

        await Assert.That(report.IsValid).IsFalse();
        await Assert.That(report.Issues.Any(issue => issue.Code is "baseline.length" or "baseline.hash")).IsTrue();
    }

    [Test]
    public async Task IdRegistry_ReusesStableAllocation()
    {
        using var workspace = TestWorkspace.Create();
        var registry = new IdRegistry
        {
            Ranges = new Dictionary<string, IdRange> { ["crafts"] = new() { Start = 9_100_000, End = 9_100_010 } }
        };
        var service = new IdRegistryService();

        var first = service.Allocate(registry, workspace.BaselinePath, "crafts", "recipe:test");
        var second = service.Allocate(registry, workspace.BaselinePath, "crafts", "recipe:test");

        await Assert.That(first.Id).IsEqualTo(9_100_000u);
        await Assert.That(second.Id).IsEqualTo(first.Id);
    }

    [Test]
    public async Task Build_SameSources_ProducesSameArtifactHash()
    {
        using var workspace = TestWorkspace.Create();
        var service = new BuildService();

        var first = service.Build(workspace.CreateBuildRequest()).Manifest.ArtifactSha256;
        var second = service.Build(workspace.CreateBuildRequest()).Manifest.ArtifactSha256;

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task SearchEverything_FindsItemRecipeAndWorkbenchByOrdinaryName()
    {
        using var workspace = TestWorkspace.Create();

        var response = new CatalogSearchService().SearchEverything(workspace.BaselinePath, "moonlight dust");

        await Assert.That(response.Results.Any(result => result.Table == "items" && result.Id == 10)).IsTrue();
        await Assert.That(response.Results.Any(result => result.Table == "crafts" && result.Id == 100 && result.Context!.Contains("Uses", StringComparison.Ordinal))).IsTrue();
        await Assert.That(response.Results.Any(result => result.Kind == "workbench" && result.Id == 300)).IsTrue();
    }

    [Test]
    public async Task SearchEverything_RecoversFromSmallSpellingMistake()
    {
        using var workspace = TestWorkspace.Create();

        var response = new CatalogSearchService().SearchEverything(workspace.BaselinePath, "archemu");

        await Assert.That(response.UsedFuzzyMatching).IsTrue();
        await Assert.That(response.Results.Any(result => result.Table == "items" && result.Id == 10)).IsTrue();
    }
}

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
        BaselinePath = Path.Combine(root, "compact.sqlite3");
        DescriptorPath = Path.Combine(root, "baseline.json");
        ProjectPath = Path.Combine(root, "project", "project.json");
        OutputPath = Path.Combine(root, "output");
    }

    public string Root { get; }
    public string BaselinePath { get; }
    public string DescriptorPath { get; }
    public string ProjectPath { get; }
    public string OutputPath { get; }

    public static TestWorkspace Create()
    {
        var workspace = new TestWorkspace(Path.Combine(Path.GetTempPath(), "aaemu-content-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.ProjectPath)!);
        workspace.CreateDatabase();
        workspace.CreateSources();
        return workspace;
    }

    public ContentBuildRequest CreateBuildRequest() => new()
    {
        BaselinePath = BaselinePath,
        BaselineDescriptorPath = DescriptorPath,
        ProjectPath = ProjectPath,
        OutputDirectory = OutputPath
    };

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={BaselinePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE items (id INTEGER, name TEXT, category_id INTEGER, price INTEGER);
            CREATE TABLE crafts (id INTEGER, title TEXT, cast_delay INTEGER, tool_id INTEGER, skill_id INTEGER, wi_id INTEGER, desc TEXT, milestone_id INTEGER, req_doodad_id INTEGER, need_bind TEXT, ac_id INTEGER, actability_limit INTEGER, show_upper_crafts TEXT, recommend_level INTEGER, visible_order INTEGER, translate TEXT);
            CREATE TABLE skills (id INTEGER, name TEXT, consume_lp INTEGER, casting_time INTEGER);
            CREATE TABLE skill_effects (id INTEGER, skill_id INTEGER, effect_id INTEGER);
            CREATE TABLE craft_materials (id INTEGER, craft_id INTEGER, item_id INTEGER, amount INTEGER, main_grade TEXT, require_grade INTEGER);
            CREATE TABLE craft_products (id INTEGER, craft_id INTEGER, item_id INTEGER, amount INTEGER, rate INTEGER, show_lower_crafts TEXT, use_grade TEXT, item_grade_id INTEGER);
            CREATE TABLE craft_packs (id INTEGER, name TEXT);
            CREATE TABLE craft_pack_crafts (id INTEGER, craft_pack_id INTEGER, craft_id INTEGER);
            CREATE TABLE doodad_almighties (id INTEGER, name TEXT, model TEXT);
            CREATE TABLE doodad_func_groups (id INTEGER, model TEXT, doodad_almighty_id INTEGER, doodad_func_group_kind_id INTEGER);
            CREATE TABLE doodad_funcs (id INTEGER, doodad_func_group_id INTEGER, actual_func_id INTEGER, actual_func_type TEXT, next_phase INTEGER, func_skill_id INTEGER);
            CREATE TABLE doodad_phase_funcs (id INTEGER, doodad_func_group_id INTEGER, actual_func_id INTEGER, actual_func_type TEXT);
            CREATE TABLE doodad_func_craft_packs (id INTEGER, craft_pack_id INTEGER);
            CREATE TABLE localized_texts (id INTEGER, tbl_name TEXT, tbl_column_name TEXT, idx INTEGER, ko TEXT, ko_ver INTEGER, en_us TEXT, en_us_ver INTEGER, zh_cn TEXT, zh_cn_ver INTEGER, ja TEXT, ja_ver INTEGER, ru TEXT, ru_ver INTEGER, zh_tw TEXT, zh_tw_ver INTEGER, de TEXT, de_ver INTEGER, fr TEXT, fr_ver INTEGER);
            INSERT INTO items VALUES (10, 'Input', 1, 1), (11, 'Output', 1, 1);
            INSERT INTO skills VALUES (200, 'Craft', 5, 1000);
            INSERT INTO skill_effects VALUES (201, 200, 1);
            INSERT INTO crafts VALUES (100, 'Source Recipe', 1000, 0, 200, 0, '', 0, 300, 0, 0, 0, 0, 1, 1, 0);
            INSERT INTO craft_materials VALUES (400, 100, 10, 2, 0, -1);
            INSERT INTO craft_products VALUES (401, 100, 11, 1, 100, 0, 0, 0);
            INSERT INTO craft_packs VALUES (90, 'source_pack');
            INSERT INTO craft_pack_crafts VALUES (402, 90, 100);
            INSERT INTO doodad_almighties VALUES (300, 'Source Bench', 'source.model');
            INSERT INTO doodad_func_groups VALUES (301, 'source.model', 300, 1);
            INSERT INTO doodad_func_craft_packs VALUES (302, 90);
            INSERT INTO doodad_funcs VALUES (303, 301, 302, 'DoodadFuncCraftPack', -1, 0);
            INSERT INTO doodad_phase_funcs VALUES (304, 301, 1, 'DoodadFuncTimer');
            INSERT INTO localized_texts (id, tbl_name, tbl_column_name, idx, en_us) VALUES
              (500, 'items', 'name', 10, 'Moonlight Archeum Dust'),
              (501, 'items', 'name', 11, 'Unstable Solution'),
              (502, 'crafts', 'title', 100, 'Archeum Tonic'),
              (503, 'doodad_almighties', 'name', 300, 'Alchemy Workbench');
            """;
        command.ExecuteNonQuery();
    }

    private void CreateSources()
    {
        var descriptor = new BaselineDescriptor
        {
            Key = "test",
            ClientBuild = "test",
            Length = new FileInfo(BaselinePath).Length,
            Sha256 = FileHashService.CalculateSha256(BaselinePath),
            TableCount = 14,
            RequiredTables = []
        };
        File.WriteAllText(DescriptorPath, ContentStudioJson.Serialize(descriptor));

        var projectDirectory = Path.GetDirectoryName(ProjectPath)!;
        Directory.CreateDirectory(Path.Combine(projectDirectory, "recipes"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "workbenches"));
        File.WriteAllText(ProjectPath, ContentStudioJson.Serialize(new ContentProjectDefinition
        {
            Key = "test",
            Name = "Test",
            TargetBaseline = "test"
        }));
        File.WriteAllText(Path.Combine(projectDirectory, "id-registry.json"), ContentStudioJson.Serialize(new IdRegistry
        {
            Ranges = new Dictionary<string, IdRange>
            {
                ["crafts"] = Range(9_100_000),
                ["doodad_almighties"] = Range(9_200_000),
                ["craft_packs"] = Range(9_300_000),
                ["skills"] = Range(9_400_000),
                ["skill_effects"] = Range(9_500_000),
                ["localized_texts"] = new IdRange { Start = 9_600_000, End = 9_600_001 },
                ["craft_materials"] = Range(9_700_000),
                ["craft_products"] = Range(9_750_000),
                ["craft_pack_crafts"] = Range(9_800_000),
                ["doodad_func_groups"] = Range(9_850_000),
                ["doodad_funcs"] = Range(9_875_000),
                ["doodad_phase_funcs"] = Range(9_900_000),
                ["doodad_func_craft_packs"] = Range(9_925_000)
            },
            Allocations = new Dictionary<string, Dictionary<string, uint>>
            {
                ["crafts"] = Allocation("recipe.test-recipe:row", 9_100_000),
                ["doodad_almighties"] = Allocation("workbench.test-workbench:row", 9_200_000),
                ["craft_packs"] = Allocation("workbench.test-workbench:craft-pack", 9_300_000),
                ["skills"] = Allocation("recipe.test-recipe:skill", 9_400_000),
                ["skill_effects"] = Allocation("recipe.test-recipe:skill-effect:0", 9_500_000),
                ["localized_texts"] = new Dictionary<string, uint>
                {
                    ["recipe.test-recipe:title"] = 9_600_000,
                    ["workbench.test-workbench:name"] = 9_600_001
                },
                ["craft_materials"] = Allocation("recipe.test-recipe:material:0", 9_700_000),
                ["craft_products"] = Allocation("recipe.test-recipe:product:0", 9_750_000),
                ["craft_pack_crafts"] = Allocation("workbench.test-workbench:pack-link:0", 9_800_000),
                ["doodad_func_groups"] = Allocation("workbench.test-workbench:group:301", 9_850_000),
                ["doodad_funcs"] = Allocation("workbench.test-workbench:func:303", 9_875_000),
                ["doodad_phase_funcs"] = Allocation("workbench.test-workbench:phase-func:304", 9_900_000),
                ["doodad_func_craft_packs"] = Allocation("workbench.test-workbench:craft-pack-payload:302", 9_925_000)
            }
        }));

        var recipe = new RecipeDefinition
        {
            Key = "recipe.test-recipe",
            Id = 9_100_000,
            Names = new Dictionary<string, string> { ["en_us"] = "Test Recipe" },
            SkillId = 9_400_000,
            SkillClone = new SkillCloneDefinition { SourceId = 200, Id = 9_400_000, SkillEffectRowIds = [9_500_000] },
            RequiredDoodadId = 9_200_000,
            RowIds = new RecipeRowIds { Localization = new Dictionary<string, uint> { ["title"] = 9_600_000 } },
            Materials = [new RecipeMaterialDefinition { Id = 9_700_000, ItemId = 10, Amount = 2 }],
            Products = [new RecipeProductDefinition { Id = 9_750_000, ItemId = 11, Amount = 1 }]
        };
        File.WriteAllText(Path.Combine(projectDirectory, "recipes", "test.json"), ContentStudioJson.Serialize(recipe));

        var workbench = new WorkbenchDefinition
        {
            Key = "workbench.test-workbench",
            Id = 9_200_000,
            SourceDoodadId = 300,
            Names = new Dictionary<string, string> { ["en_us"] = "Test Workbench" },
            CraftPack = new WorkbenchCraftPackDefinition { Id = 9_300_000, Name = "test_pack" },
            RecipeIds = [9_100_000],
            RowIds = new WorkbenchRowIds
            {
                FunctionGroups = new Dictionary<uint, uint> { [301] = 9_850_000 },
                Functions = new Dictionary<uint, uint> { [303] = 9_875_000 },
                PhaseFunctions = new Dictionary<uint, uint> { [304] = 9_900_000 },
                CraftPackPayloads = new Dictionary<uint, uint> { [302] = 9_925_000 },
                Localization = new Dictionary<string, uint> { ["name"] = 9_600_001 },
                CraftPackLinks = [9_800_000]
            }
        };
        File.WriteAllText(Path.Combine(projectDirectory, "workbenches", "test.json"), ContentStudioJson.Serialize(workbench));
    }

    private static IdRange Range(uint id) => new() { Start = id, End = id };

    private static Dictionary<string, uint> Allocation(string key, uint id) => new() { [key] = id };
}
