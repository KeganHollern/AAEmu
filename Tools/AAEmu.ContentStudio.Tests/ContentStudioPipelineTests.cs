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
    public async Task RecipeWorkbenchAssignment_MovesRecipeToSelectedWorkbenchMenu()
    {
        using var workspace = TestWorkspace.Create();
        var recipe = new RecipeDefinition
        {
            CraftPackIds = [19],
            RequiredDoodadId = 558
        };
        var service = new RecipeWorkbenchService();

        var existing = service.Assign(recipe, workspace.BaselinePath, workspace.ProjectPath, 300);

        await Assert.That(existing.WorkbenchName).IsEqualTo("Alchemy Workbench");
        await Assert.That(recipe.RequiredDoodadId).IsEqualTo(300u);
        await Assert.That(recipe.CraftPackIds).IsEquivalentTo(new uint[] { 90 });

        var custom = service.Assign(recipe, workspace.BaselinePath, workspace.ProjectPath, 9_200_000);

        await Assert.That(custom.WorkbenchName).IsEqualTo("Test Workbench");
        await Assert.That(recipe.RequiredDoodadId).IsEqualTo(9_200_000u);
        await Assert.That(recipe.CraftPackIds).IsEquivalentTo(new uint[] { 9_300_000 });
    }

    [Test]
    public async Task BuiltDatabaseValidation_RejectsRecipeListedAtWrongWorkbenchMenu()
    {
        using var workspace = TestWorkspace.Create();
        var result = new BuildService().Build(workspace.CreateBuildRequest());
        using (var connection = new SqliteConnection($"Data Source={result.ArtifactPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO craft_pack_crafts VALUES (9999999, 90, 9100000);";
            command.ExecuteNonQuery();
        }
        var project = new ProjectRepository().LoadProject(workspace.ProjectPath);

        var report = new ContentValidator().ValidateBuiltDatabase(result.ArtifactPath, project);

        await Assert.That(report.Issues.Any(issue => issue.Code == "recipe.workbenchMenuMismatch")).IsTrue();
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
    public async Task Build_RejectsMidBuildRawSqlEditWithoutPromotingOutputs()
    {
        using var workspace = TestWorkspace.Create();
        var rawSqlDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "raw-sql");
        Directory.CreateDirectory(rawSqlDirectory);
        var rawSqlPath = Path.Combine(rawSqlDirectory, "skill-balance.sql");
        File.WriteAllText(rawSqlPath, "UPDATE skills SET mana_cost = 16 WHERE id = 200;");
        Directory.CreateDirectory(workspace.OutputPath);
        var artifactPath = Path.Combine(workspace.OutputPath, "compact.test.sqlite3");
        var manifestPath = Path.Combine(workspace.OutputPath, "content-build-manifest.json");
        File.WriteAllText(artifactPath, "previous artifact");
        File.WriteAllText(manifestPath, "previous manifest");
        var service = new BuildService(() => File.WriteAllText(rawSqlPath, "UPDATE skills SET mana_cost = 17 WHERE id = 200;"));

        var exception = Assert.Throws<ContentStudioException>(() => service.Build(workspace.CreateBuildRequest()));

        await Assert.That(exception!.Message).Contains("Project source changed");
        await Assert.That(File.ReadAllText(artifactPath)).IsEqualTo("previous artifact");
        await Assert.That(File.ReadAllText(manifestPath)).IsEqualTo("previous manifest");
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "content-build-report.md"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "content-build-audit.sql"))).IsFalse();
    }

    [Test]
    public async Task Build_RejectsStagedArtifactEditWithoutPromotingOutputs()
    {
        using var workspace = TestWorkspace.Create();
        var service = new BuildService(() =>
        {
            var stagingPath = Directory.GetFiles(
                Path.Combine(workspace.OutputPath, ".staging"),
                "compact.sqlite3",
                SearchOption.AllDirectories).Single();
            File.AppendAllText(stagingPath, "concurrent artifact edit");
        });

        var exception = Assert.Throws<ContentStudioException>(() => service.Build(workspace.CreateBuildRequest()));

        await Assert.That(exception!.Message).Contains("Build output changed");
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "compact.test.sqlite3"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "content-build-manifest.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "content-build-report.md"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(workspace.OutputPath, "content-build-audit.sql"))).IsFalse();
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

    [Test]
    public async Task SearchEverything_ListsAbilitiesAndTheirSkills()
    {
        using var workspace = TestWorkspace.Create();
        var search = new CatalogSearchService().SearchEverything(workspace.BaselinePath, "abilities");
        var ability = new CatalogRecordService().GetAbility(workspace.BaselinePath, 1);

        await Assert.That(search.Results.Any(result => result.Kind == "ability" && result.Name == "Battlerage")).IsTrue();
        await Assert.That(ability).IsNotNull();
        await Assert.That(ability!.Skills.Any(skill => skill.Id == 200 && skill.Name == "Whirlwind Slash")).IsTrue();
    }

    [Test]
    public async Task FriendlyCloneLookups_FindNamedSourcesAndLoadEveryDirectRecipe()
    {
        using var workspace = TestWorkspace.Create();
        using (var connection = new SqliteConnection($"Data Source={workspace.BaselinePath};Pooling=False"))
        {
            connection.Open();
            for (var index = 0; index < 12; index++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO crafts VALUES (@id, @title, 1000, 0, 200, 0, '', 0, 300, 0, 0, 0, 0, 1, 1, 0); INSERT INTO craft_materials VALUES (@rowId, @id, 10, 1, 0, -1);";
                command.Parameters.AddWithValue("@id", 1_000 + index);
                command.Parameters.AddWithValue("@title", $"Moonlight Part {index + 1}");
                command.Parameters.AddWithValue("@rowId", 2_000 + index);
                command.ExecuteNonQuery();
            }
        }

        var catalog = new CompactCatalogService();
        var recipes = catalog.SearchRecipes(workspace.BaselinePath, "Archeum Tonic");
        var workbenches = catalog.SearchWorkbenches(workspace.BaselinePath, "Alchemy Workbench");
        var relationships = catalog.GetRecipesConsumingItem(workspace.BaselinePath, 10);
        var draft = new ScaffoldService().CreateRecipeDraft(workspace.BaselinePath, 100);

        await Assert.That(recipes.Any(recipe => recipe.Id == 100 && recipe.Name == "Archeum Tonic")).IsTrue();
        await Assert.That(workbenches.Any(workbench => workbench.Id == 300 && workbench.Name == "Alchemy Workbench")).IsTrue();
        await Assert.That(relationships.Count).IsEqualTo(13);
        await Assert.That(draft.Materials).HasSingleItem();
        await Assert.That(draft.Products).HasSingleItem();
        await Assert.That(draft.Names["en_us"]).IsEqualTo("Custom Archeum Tonic");
    }

    [Test]
    public async Task ItemReferencePicker_RanksExactNamesAndClassifiesOnlyItemTemplates()
    {
        using var workspace = TestWorkspace.Create();
        var results = new CompactCatalogService().SearchItems(workspace.BaselinePath, "Moonlight Archeum Dust");

        await Assert.That(results.First().Id).IsEqualTo(10u);
        await Assert.That(CatalogRecordService.ReferenceTableFor("required_item_id")).IsEqualTo("items");
        await Assert.That(CatalogRecordService.ReferenceTableFor("consume_item_id")).IsEqualTo("items");
        await Assert.That(CatalogRecordService.ReferenceTableFor("item_grade_id")).IsEqualTo("item_grades");
        await Assert.That(CatalogRecordService.ReferenceTableFor("starter_item_pack_id")).IsNull();
    }

    [Test]
    public async Task ItemGameplayProfile_ExplainsGearStatsEffectsAndSetBonuses()
    {
        using var workspace = TestWorkspace.Create();
        var profile = new ItemGameplayService().GetProfile(workspace.BaselinePath, 12)!;

        await Assert.That(profile.IsEquipment).IsTrue();
        await Assert.That(profile.GearKind).IsEqualTo("Weapon");
        await Assert.That(profile.StatWeights.Any(stat => stat.Name == "Intelligence" && stat.Percentage == 67)).IsTrue();
        await Assert.That(profile.StatWeights.Any(stat => stat.Name == "Spirit" && stat.Percentage == 33)).IsTrue();
        await Assert.That(profile.EquipmentSet).IsNotNull();
        await Assert.That(profile.EquipmentSet!.Pieces.Any(piece => piece.Id == 12)).IsTrue();
        await Assert.That(profile.EquipmentSet.Bonuses.Single().Buff!.Name).IsEqualTo("Wave Wisdom");
        await Assert.That(profile.Effects.Single(effect => effect.Source == "Weapon proc").Name).IsEqualTo("Wave Burst");
    }

    [Test]
    public async Task GearEditor_CreatesPrivateStatProfileAndKeepsSharedProfileUnchanged()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "items", 12)!;
        var linked = record.LinkedRecords.Single();

        await Assert.That(record.RelatedSections.Single(section => section.IsEquipmentTemplate).Table).IsEqualTo("item_weapons");
        await Assert.That(linked.SourceId).IsEqualTo(30u);
        await Assert.That(linked.ReferenceCount).IsEqualTo(1);

        linked.Fields.Single(field => field.Name == "int_weight").Value = "1";
        linked.Fields.Single(field => field.Name == "spi_weight").Value = "3";
        var saved = new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = record.Table,
            SourceId = record.Id,
            Mode = RecordChangeMode.Modify,
            DisplayName = record.Name,
            Values = record.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value),
            Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values),
            Children = record.RelatedSections.SelectMany(section => section.Rows.Select(row => new RecordChildDraft
            {
                Table = section.Table,
                OwnerColumn = section.OwnerColumn,
                SourceId = row.Id,
                Values = row.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value)
            })).ToList(),
            LinkedRecords =
            [
                new RecordLinkedDraft
                {
                    Table = linked.Table,
                    SourceId = linked.SourceId,
                    LinkTable = linked.LinkTable,
                    LinkSourceId = linked.LinkSourceId,
                    LinkColumn = linked.LinkColumn,
                    Values = linked.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value)
                }
            ]
        });

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = new SqliteConnection($"Data Source={build.ArtifactPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mod_set_id FROM item_weapons WHERE item_id = 12;";
        var privateProfileId = Convert.ToUInt32(command.ExecuteScalar());
        command.CommandText = "SELECT int_weight || ':' || spi_weight FROM equip_item_attr_modifiers WHERE id = @id;";
        command.Parameters.AddWithValue("@id", privateProfileId);
        var privateWeights = Convert.ToString(command.ExecuteScalar());
        command.Parameters.Clear();
        command.CommandText = "SELECT int_weight || ':' || spi_weight FROM equip_item_attr_modifiers WHERE id = 30;";
        var sharedWeights = Convert.ToString(command.ExecuteScalar());

        await Assert.That(saved.Id).IsEqualTo(12u);
        await Assert.That(privateProfileId).IsNotEqualTo(30u);
        await Assert.That(privateWeights).IsEqualTo("1:3");
        await Assert.That(sharedWeights).IsEqualTo("2:1");
    }

    [Test]
    public async Task GearEditor_DisablingSavedPrivateStatProfileRemovesItsClone()
    {
        using var workspace = TestWorkspace.Create();
        var catalog = new CatalogRecordService();
        var record = catalog.GetRecord(workspace.BaselinePath, "items", 12)!;
        var linked = record.LinkedRecords.Single();
        var service = new RecordScaffoldService();
        var linkedDraft = new RecordLinkedDraft
        {
            Table = linked.Table,
            SourceId = linked.SourceId,
            LinkTable = linked.LinkTable,
            LinkSourceId = linked.LinkSourceId,
            LinkColumn = linked.LinkColumn,
            Values = linked.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value)
        };
        var initial = service.Save(CreateRecordRequest(workspace, record, [linkedDraft]));
        var manifests = new ManifestService();
        var snapshot = manifests.ReadSnapshot(initial.Path);
        var definition = ContentStudioJson.Deserialize<RecordDefinition>(snapshot.Contents, initial.Path);
        var privateProfileId = definition.LinkedClones.Single().Id;

        service.Update(initial.Path, definition, CreateRecordRequest(workspace, record, []), snapshot.Version);
        var disabledOnce = manifests.ReadSnapshot(initial.Path);
        var disabledDefinition = ContentStudioJson.Deserialize<RecordDefinition>(disabledOnce.Contents, initial.Path);
        service.Update(initial.Path, disabledDefinition, CreateRecordRequest(workspace, record, [linkedDraft]), disabledOnce.Version);
        var enabledAgain = manifests.ReadSnapshot(initial.Path);
        var enabledDefinition = ContentStudioJson.Deserialize<RecordDefinition>(enabledAgain.Contents, initial.Path);
        var secondPrivateProfileId = enabledDefinition.LinkedClones.Single().Id;
        service.Update(initial.Path, enabledDefinition, CreateRecordRequest(workspace, record, []), enabledAgain.Version);
        var updated = ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(initial.Path), initial.Path);
        var project = new ProjectRepository().LoadProject(workspace.ProjectPath);
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = CompactConnectionFactory.OpenReadOnly(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mod_set_id FROM item_weapons WHERE item_id = 12;";
        var linkedProfileId = Convert.ToUInt32(command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM equip_item_attr_modifiers WHERE id = @id;";
        command.Parameters.AddWithValue("@id", privateProfileId);
        var privateProfileRows = Convert.ToInt32(command.ExecuteScalar());

        await Assert.That(updated.LinkedClones).IsEmpty();
        await Assert.That(project.Registry.Allocations["equip_item_attr_modifiers"].Values.Contains(privateProfileId)).IsFalse();
        await Assert.That(project.Registry.Tombstones["equip_item_attr_modifiers"].Values.Contains(privateProfileId)).IsTrue();
        await Assert.That(secondPrivateProfileId).IsNotEqualTo(privateProfileId);
        await Assert.That(project.Registry.Tombstones["equip_item_attr_modifiers"].Values.Contains(secondPrivateProfileId)).IsTrue();
        await Assert.That(linkedProfileId).IsEqualTo(30u);
        await Assert.That(privateProfileRows).IsEqualTo(0);
    }

    [Test]
    public async Task RecordEditor_RestoresManifestAndRegistryWhenAtomicUpdateFails()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "items", 12)!;
        var linked = record.LinkedRecords.Single();
        var initial = new RecordScaffoldService().Save(CreateRecordRequest(workspace, record,
        [
            new RecordLinkedDraft
            {
                Table = linked.Table,
                SourceId = linked.SourceId,
                LinkTable = linked.LinkTable,
                LinkSourceId = linked.LinkSourceId,
                LinkColumn = linked.LinkColumn,
                Values = linked.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value)
            }
        ]));
        var manifests = new ManifestService();
        var snapshot = manifests.ReadSnapshot(initial.Path);
        var definition = ContentStudioJson.Deserialize<RecordDefinition>(snapshot.Contents, initial.Path);
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedManifest = File.ReadAllText(initial.Path);
        var expectedRegistry = File.ReadAllText(registryPath);
        var service = new RecordScaffoldService((index, _) =>
        {
            if (index == 1) throw new IOException("Simulated registry replacement failure.");
        });

        var exception = Assert.Throws<ContentStudioException>(() =>
            service.Update(initial.Path, definition, CreateRecordRequest(workspace, record, []), snapshot.Version));

        await Assert.That(exception!.Message).Contains("All project files were restored");
        await Assert.That(File.ReadAllText(initial.Path)).IsEqualTo(expectedManifest);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task RecordEditor_CanonicalizesCaseVariantTablesBeforeAllocatingIds()
    {
        using var workspace = TestWorkspace.Create();
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var registry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(registryPath), registryPath);
        registry.Ranges["equip_item_attr_modifiers"] = new IdRange { Start = 8_000_000, End = 8_000_010 };
        File.WriteAllText(registryPath, ContentStudioJson.Serialize(registry));
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "items", 12)!;
        var linked = record.LinkedRecords.Single();
        var request = CreateRecordRequest(workspace, record,
        [
            new RecordLinkedDraft
            {
                Table = "EQUIP_ITEM_ATTR_MODIFIERS",
                SourceId = linked.SourceId,
                LinkTable = "ITEM_WEAPONS",
                LinkSourceId = linked.LinkSourceId,
                LinkColumn = "MOD_SET_ID",
                Values = linked.Fields.Where(field => !field.IsIdentity && field.IsEditable)
                    .ToDictionary(field => field.Name.ToUpperInvariant(), field => field.Value)
            }
        ]);
        request.Table = "ITEMS";

        var saved = new RecordScaffoldService().Save(request);
        var definition = ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(saved.Path), saved.Path);
        var storedRegistry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(registryPath), registryPath);

        await Assert.That(definition.Table).IsEqualTo("items");
        await Assert.That(definition.LinkedClones.Single().Table).IsEqualTo("equip_item_attr_modifiers");
        await Assert.That(definition.LinkedClones.Single().LinkTable).IsEqualTo("item_weapons");
        await Assert.That(definition.LinkedClones.Single().LinkColumn).IsEqualTo("mod_set_id");
        await Assert.That(storedRegistry.Ranges.Keys.Count(table => table.Equals("equip_item_attr_modifiers", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        await Assert.That(storedRegistry.Ranges.Keys.Contains("equip_item_attr_modifiers")).IsTrue();
        await Assert.That(storedRegistry.Allocations.Keys.Count(table => table.Equals("equip_item_attr_modifiers", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        await Assert.That(storedRegistry.Allocations.Keys.Contains("equip_item_attr_modifiers")).IsTrue();
    }

    [Test]
    public async Task RecordEditor_PreservesRegistryEditMadeWhileSaveIsPrepared()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        var injected = false;
        var service = new RecordScaffoldService((index, path) =>
        {
            if (index != -2 || injected) return;
            injected = true;
            var registry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(path), path);
            registry.Ranges["external_agent_table"] = new IdRange { Start = 7_000_000, End = 7_000_010 };
            File.WriteAllText(path, ContentStudioJson.Serialize(registry));
        });

        service.Save(CreateRecordRequest(workspace, record, []));
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var savedRegistry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(registryPath), registryPath);

        await Assert.That(savedRegistry.Ranges.ContainsKey("external_agent_table")).IsTrue();
        await Assert.That(savedRegistry.Ranges.ContainsKey("skills")).IsTrue();
    }

    [Test]
    public async Task RecordEditor_ModifiesLegitimateZeroIdBalanceRow()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "item_grades", 0)!;
        record.Fields.Single(field => field.Name == "stat_multiplier").Value = "125";

        new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = record.Table,
            SourceId = record.Id,
            Mode = RecordChangeMode.Modify,
            DisplayName = "Basic grade",
            Values = record.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value)
        });
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var changed = new CatalogRecordService().GetRecord(build.ArtifactPath, "item_grades", 0)!;
        var diff = new DatabaseDiffService().Compare(workspace.BaselinePath, build.ArtifactPath);
        var gradeDiff = diff.Tables.Single(table => table.Table == "item_grades");

        await Assert.That(changed.Fields.Single(field => field.Name == "stat_multiplier").Value).IsEqualTo("125");
        await Assert.That(gradeDiff.ModifiedRows).IsEqualTo(1);
        await Assert.That(gradeDiff.ChangedCells.Any(cell => cell.Id == 0 && cell.Column == "stat_multiplier" && cell.ArtifactValue == "125")).IsTrue();
    }

    [Test]
    public async Task DeleteRecipe_DetachesWorkbenchAndRetiresEveryOwnedId()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var deletion = new ChangeDeletionService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");

        var preview = deletion.Preview(workspace.ProjectPath, recipePath);
        var result = deletion.Delete(workspace.ProjectPath, recipePath, preview.Version);
        var project = new ProjectRepository().LoadProject(workspace.ProjectPath);

        await Assert.That(preview.CanDelete).IsTrue();
        await Assert.That(preview.Consequences.Any(value => value.Contains("removed from 1 saved workbench", StringComparison.Ordinal))).IsTrue();
        await Assert.That(File.Exists(recipePath)).IsFalse();
        await Assert.That(project.Workbenches.Single().RecipeIds).IsEmpty();
        await Assert.That(project.Workbenches.Single().RowIds.CraftPackLinks).IsEmpty();
        await Assert.That(project.Registry.Allocations["crafts"].ContainsKey("recipe.test-recipe:row")).IsFalse();
        await Assert.That(project.Registry.Tombstones["crafts"]["recipe.test-recipe:row"]).IsEqualTo(9_100_000u);
        await Assert.That(project.Registry.Tombstones["craft_pack_crafts"]["workbench.test-workbench:pack-link:0"]).IsEqualTo(9_800_000u);
        await Assert.That(result.UpdatedChangeCount).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteWorkbench_KeepsRecipeAndRemovesItsWorkbenchRequirement()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var deletion = new ChangeDeletionService();
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");

        var preview = deletion.Preview(workspace.ProjectPath, workbenchPath);
        var result = deletion.Delete(workspace.ProjectPath, workbenchPath, preview.Version);
        var project = new ProjectRepository().LoadProject(workspace.ProjectPath);

        await Assert.That(preview.CanDelete).IsTrue();
        await Assert.That(File.Exists(workbenchPath)).IsFalse();
        await Assert.That(project.Recipes.Single().RequiredDoodadId).IsEqualTo(0u);
        await Assert.That(project.Registry.Tombstones["doodad_almighties"]["workbench.test-workbench:row"]).IsEqualTo(9_200_000u);
        await Assert.That(result.UpdatedChangeCount).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteRecreatedChange_PreservesEveryRetiredId()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var deletion = new ChangeDeletionService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var originalRecipe = ContentStudioJson.Deserialize<RecipeDefinition>(File.ReadAllText(recipePath), recipePath);

        var firstPreview = deletion.Preview(workspace.ProjectPath, recipePath);
        deletion.Delete(workspace.ProjectPath, recipePath, firstPreview.Version);

        const uint recreatedId = 9_100_001;
        originalRecipe.Id = recreatedId;
        File.WriteAllText(recipePath, ContentStudioJson.Serialize(originalRecipe) + Environment.NewLine);
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var registry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(registryPath), registryPath);
        registry.Allocations["crafts"]["recipe.test-recipe:row"] = recreatedId;
        File.WriteAllText(registryPath, ContentStudioJson.Serialize(registry) + Environment.NewLine);

        var secondPreview = deletion.Preview(workspace.ProjectPath, recipePath);
        deletion.Delete(workspace.ProjectPath, recipePath, secondPreview.Version);
        var finalRegistry = new ProjectRepository().LoadProject(workspace.ProjectPath).Registry;
        var retiredCraftIds = finalRegistry.Tombstones["crafts"].Values.ToHashSet();

        await Assert.That(retiredCraftIds.Contains(9_100_000u)).IsTrue();
        await Assert.That(retiredCraftIds.Contains(recreatedId)).IsTrue();
    }

    [Test]
    public async Task DeleteChange_RejectsStalePreviewWithoutChangingRelatedFiles()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var deletion = new ChangeDeletionService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var preview = deletion.Preview(workspace.ProjectPath, recipePath);
        var recipe = ContentStudioJson.Deserialize<RecipeDefinition>(manifests.Read(recipePath), recipePath);
        recipe.Names["en_us"] = "Agent Updated Recipe";
        manifests.Save(recipePath, ContentStudioJson.Serialize(recipe));
        var expectedRecipe = File.ReadAllText(recipePath);
        var expectedWorkbench = File.ReadAllText(workbenchPath);
        var expectedRegistry = File.ReadAllText(registryPath);

        var exception = Assert.Throws<ContentStudioException>(() => deletion.Delete(workspace.ProjectPath, recipePath, preview.Version));

        await Assert.That(exception!.Message).Contains("updated outside this editor");
        await Assert.That(File.ReadAllText(recipePath)).IsEqualTo(expectedRecipe);
        await Assert.That(File.ReadAllText(workbenchPath)).IsEqualTo(expectedWorkbench);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task DeleteChange_RejectsEditAfterPreviewWithoutDeletingNewContents()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedWorkbench = File.ReadAllText(workbenchPath);
        var expectedRegistry = File.ReadAllText(registryPath);
        var deletion = new ChangeDeletionService((index, path) =>
        {
            if (index != -1) return;
            var current = manifests.ReadSnapshot(path);
            var recipe = ContentStudioJson.Deserialize<RecipeDefinition>(current.Contents, path);
            recipe.Names["en_us"] = "Concurrent Recipe Edit";
            manifests.Save(path, ContentStudioJson.Serialize(recipe), current.Version);
        });
        var preview = deletion.Preview(workspace.ProjectPath, recipePath);

        var exception = Assert.Throws<ContentStudioException>(() => deletion.Delete(workspace.ProjectPath, recipePath, preview.Version));

        await Assert.That(exception!.Message).Contains("updated outside this editor");
        await Assert.That(File.ReadAllText(recipePath)).Contains("Concurrent Recipe Edit");
        await Assert.That(File.ReadAllText(workbenchPath)).IsEqualTo(expectedWorkbench);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task DeleteChange_RejectsNewProjectSourceAddedDuringPromotion()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedRecipe = File.ReadAllText(recipePath);
        var expectedWorkbench = File.ReadAllText(workbenchPath);
        var expectedRegistry = File.ReadAllText(registryPath);
        var addedPath = Path.Combine(Path.GetDirectoryName(workbenchPath)!, "concurrent.json");
        var deletion = new ChangeDeletionService((index, _) =>
        {
            if (index != 2) return;
            File.WriteAllText(addedPath, ContentStudioJson.Serialize(new WorkbenchDefinition
            {
                Key = "workbench.concurrent",
                Id = 7_000_000,
                CraftPack = new WorkbenchCraftPackDefinition { Id = 7_000_001 },
                RecipeIds = [9_100_000]
            }));
        });
        var preview = deletion.Preview(workspace.ProjectPath, recipePath);

        var exception = Assert.Throws<ContentStudioException>(() => deletion.Delete(workspace.ProjectPath, recipePath, preview.Version));

        await Assert.That(exception!.Message).Contains("All project files were restored");
        await Assert.That(File.ReadAllText(recipePath)).IsEqualTo(expectedRecipe);
        await Assert.That(File.ReadAllText(workbenchPath)).IsEqualTo(expectedWorkbench);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
        await Assert.That(File.Exists(addedPath)).IsTrue();
    }

    [Test]
    public async Task DeleteChange_RestoresEveryFileWhenTransactionFails()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedRecipe = File.ReadAllText(recipePath);
        var expectedWorkbench = File.ReadAllText(workbenchPath);
        var expectedRegistry = File.ReadAllText(registryPath);
        var deletion = new ChangeDeletionService((index, _) =>
        {
            if (index == 1) throw new IOException("Simulated transaction failure.");
        });
        var preview = deletion.Preview(workspace.ProjectPath, recipePath);

        var exception = Assert.Throws<ContentStudioException>(() => deletion.Delete(workspace.ProjectPath, recipePath, preview.Version));

        await Assert.That(exception!.Message).Contains("All project files were restored");
        await Assert.That(File.ReadAllText(recipePath)).IsEqualTo(expectedRecipe);
        await Assert.That(File.ReadAllText(workbenchPath)).IsEqualTo(expectedWorkbench);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task DeleteChange_DoesNotOverwriteExternalEditDuringRollback()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var recipePath = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var workbenchPath = manifests.FindByKey(workspace.ProjectPath, "workbench.test-workbench");
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedRecipe = File.ReadAllText(recipePath);
        var externalWorkbench = File.ReadAllText(workbenchPath) + Environment.NewLine;
        var expectedRegistry = File.ReadAllText(registryPath);
        var deletion = new ChangeDeletionService((index, _) =>
        {
            if (index != 1) return;
            File.WriteAllText(workbenchPath, externalWorkbench);
            throw new IOException("Simulated failure after an external edit.");
        });
        var preview = deletion.Preview(workspace.ProjectPath, recipePath);

        var exception = Assert.Throws<ContentStudioException>(() => deletion.Delete(workspace.ProjectPath, recipePath, preview.Version));

        await Assert.That(exception!.Message).Contains("could not be restored");
        await Assert.That(File.ReadAllText(workbenchPath)).IsEqualTo(externalWorkbench);
        await Assert.That(File.ReadAllText(recipePath)).IsEqualTo(expectedRecipe);
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task RecordDetails_ShowsEveryColumnAndDuplicatesSkillGraph()
    {
        using var workspace = TestWorkspace.Create();
        var catalog = new CatalogRecordService();
        var record = catalog.GetRecord(workspace.BaselinePath, "skills", 200)!;

        await Assert.That(record.Fields.Count).IsEqualTo(14);
        await Assert.That(record.Fields.Any(field => field.Name == "cooldown_time" && field.Help!.Contains("milliseconds"))).IsTrue();
        await Assert.That(record.Fields.Single(field => field.Name == "skill_controller_id").IsNull).IsTrue();
        await Assert.That(record.GameplayLinks.Single().Facts.Any(fact => fact.Label == "Protection / charge" && fact.Value == "561")).IsTrue();
        await Assert.That(record.GameplayLinks.Single().Facts.Any(fact => fact.Label == "Physical defense" && fact.Value == "+700")).IsTrue();

        record.Fields.Single(field => field.Name == "name").Value = "Custom Whirlwind Slash";
        record.Localizations.Single(field => field.Field == "name").Values["en_us"] = "Custom Whirlwind Slash";
        var saved = new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = "skills",
            SourceId = 200,
            Mode = RecordChangeMode.Duplicate,
            DisplayName = "Custom Whirlwind Slash",
            Values = record.Fields.Where(field => !field.IsIdentity).ToDictionary(field => field.Name, field => field.Value),
            Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values)
        });
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var duplicate = catalog.GetRecord(build.ArtifactPath, "skills", saved.Id)!;

        await Assert.That(saved.Id).IsEqualTo(9_400_001u);
        await Assert.That(saved.RelatedRowsCopied).IsEqualTo(1);
        await Assert.That(duplicate.Name).IsEqualTo("Custom Whirlwind Slash");
        await Assert.That(duplicate.Fields.Single(field => field.Name == "skill_controller_id").IsNull).IsTrue();
    }

    [Test]
    public async Task RecordDetails_ResolvesOnlyTablesThatExistInTheCompactSchema()
    {
        using var workspace = TestWorkspace.Create();
        var catalog = new CatalogRecordService();

        var record = catalog.GetRecord(workspace.BaselinePath, "SKILLS", 200);
        var rejected = catalog.GetRecord(workspace.BaselinePath, "skills; DROP TABLE items; --", 200);
        var itemStillExists = catalog.GetRecord(workspace.BaselinePath, "items", 12);

        await Assert.That(record).IsNotNull();
        await Assert.That(record!.Table).IsEqualTo("skills");
        await Assert.That(rejected).IsNull();
        await Assert.That(itemStillExists).IsNotNull();
    }

    [Test]
    public async Task RecordEditor_ModificationIsSparseAndDiffShowsChangedCell()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        record.Fields.Single(field => field.Name == "show").Value = "0";

        var saved = new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = "skills",
            SourceId = 200,
            Mode = RecordChangeMode.Modify,
            DisplayName = "Hidden Whirlwind Slash",
            Values = record.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value),
            Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values)
        });

        var definition = ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(saved.Path), saved.Path);
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var diff = new DatabaseDiffService().Compare(workspace.BaselinePath, build.ArtifactPath);
        var skillDiff = diff.Tables.Single(table => table.Table == "skills");

        await Assert.That(definition.Values.Count).IsEqualTo(1);
        await Assert.That(definition.Values["show"]).IsEqualTo("0");
        await Assert.That(definition.Localizations.Count).IsEqualTo(0);
        await Assert.That(skillDiff.ModifiedRows).IsEqualTo(1);
        await Assert.That(skillDiff.ChangedCells.Any(cell => cell.Id == 200 && cell.Column == "show" && cell.BaselineValue == "t" && cell.ArtifactValue == "f")).IsTrue();
    }

    [Test]
    public async Task RecordEditor_SparseLocalizationChangePreservesOtherLanguages()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        record.Localizations.Single(field => field.Field == "name").Values["en_us"] = "Customized Whirlwind Slash";

        new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = "skills",
            SourceId = 200,
            Mode = RecordChangeMode.Modify,
            DisplayName = "Customized Whirlwind Slash",
            Values = record.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value),
            Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values)
        });

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = CompactConnectionFactory.OpenReadOnly(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ko, en_us, fr FROM localized_texts WHERE tbl_name = 'skills' AND tbl_column_name = 'name' AND idx = 200;";
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("소용돌이 베기");
        await Assert.That(reader.GetString(1)).IsEqualTo("Customized Whirlwind Slash");
        await Assert.That(reader.GetString(2)).IsEqualTo("Tourbillon tranchant");
    }

    [Test]
    public async Task ArtifactValidation_RejectsMismatchedSuppliedLocalization()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        record.Localizations.Single(field => field.Field == "name").Values["en_us"] = "Expected Whirlwind Slash";
        new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = "skills",
            SourceId = 200,
            Mode = RecordChangeMode.Modify,
            DisplayName = "Expected Whirlwind Slash",
            Values = record.Fields.Where(field => !field.IsIdentity && field.IsEditable).ToDictionary(field => field.Name, field => field.Value),
            Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values)
        });
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using (var connection = new SqliteConnection($"Data Source={build.ArtifactPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE localized_texts SET en_us = 'Unexpected text' WHERE tbl_name = 'skills' AND tbl_column_name = 'name' AND idx = 200;";
            command.ExecuteNonQuery();
        }

        var project = new ProjectRepository().LoadProject(workspace.ProjectPath);
        var report = new ContentValidator().ValidateBuiltDatabase(build.ArtifactPath, project);

        await Assert.That(report.Issues.Any(issue =>
            issue.Code == "artifact.localizationMismatch" &&
            issue.Entity == project.Records.Single().Key)).IsTrue();
    }

    [Test]
    public async Task RecordEditor_RejectsConcurrentEditAtFinalSave()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        var saved = new RecordScaffoldService().Save(CreateRecordRequest(workspace, record, []));
        var manifests = new ManifestService();
        var opened = manifests.ReadSnapshot(saved.Path);
        var definition = ContentStudioJson.Deserialize<RecordDefinition>(opened.Contents, saved.Path);
        var externallyEdited = ContentStudioJson.Deserialize<RecordDefinition>(opened.Contents, saved.Path);
        externallyEdited.DisplayName = "Agent Updated Skill";
        var externalJson = ContentStudioJson.Serialize(externallyEdited);
        var service = new RecordScaffoldService((index, path) =>
        {
            if (index == 0) manifests.Save(path, externalJson, opened.Version);
        });
        var registryPath = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "id-registry.json");
        var expectedRegistry = File.ReadAllText(registryPath);

        var exception = Assert.Throws<ContentStudioException>(() =>
            service.Update(saved.Path, definition, CreateRecordRequest(workspace, record, []), opened.Version));

        await Assert.That(exception!.Message).Contains("updated outside this editor");
        await Assert.That(File.ReadAllText(saved.Path)).Contains("Agent Updated Skill");
        await Assert.That(File.ReadAllText(registryPath)).IsEqualTo(expectedRegistry);
    }

    [Test]
    public async Task RecordEditor_IgnoresForgedImmutableDefinitionWithValidVersion()
    {
        using var workspace = TestWorkspace.Create();
        var record = new CatalogRecordService().GetRecord(workspace.BaselinePath, "skills", 200)!;
        var service = new RecordScaffoldService();
        var saved = service.Save(CreateRecordRequest(workspace, record, []));
        var manifests = new ManifestService();
        var snapshot = manifests.ReadSnapshot(saved.Path);
        var forged = ContentStudioJson.Deserialize<RecordDefinition>(snapshot.Contents, saved.Path);
        forged.Key = "record.forged";
        forged.Id = 999_999;
        forged.Mode = RecordChangeMode.Duplicate;

        service.Update(saved.Path, forged, CreateRecordRequest(workspace, record, []), snapshot.Version);
        var updated = ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(saved.Path), saved.Path);

        await Assert.That(updated.Key).IsEqualTo(saved.Key);
        await Assert.That(updated.Id).IsEqualTo(200u);
        await Assert.That(updated.Mode).IsEqualTo(RecordChangeMode.Modify);
    }

    [Test]
    public async Task RecordCompiler_CanonicalizesTableFieldAndLocalizationCasing()
    {
        using var workspace = TestWorkspace.Create();
        var recordsDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "records");
        Directory.CreateDirectory(recordsDirectory);
        File.WriteAllText(Path.Combine(recordsDirectory, "uppercase.json"), ContentStudioJson.Serialize(new RecordDefinition
        {
            Key = "record.test-uppercase",
            DisplayName = "Case-safe Whirlwind Slash",
            Mode = RecordChangeMode.Modify,
            Table = "SKILLS",
            SourceId = 200,
            Id = 200,
            Values = new Dictionary<string, string?> { ["SHOW"] = "0" },
            Localizations = new Dictionary<string, Dictionary<string, string>>
            {
                ["NAME"] = new() { ["EN_US"] = "Case-safe Whirlwind Slash" }
            }
        }));

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = CompactConnectionFactory.OpenReadOnly(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT show FROM skills WHERE id = 200;";
        var show = Convert.ToString(command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*), MAX(en_us) FROM localized_texts WHERE tbl_name = 'skills' AND tbl_column_name = 'name' AND idx = 200;";
        using var reader = command.ExecuteReader();

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(show).IsEqualTo("f");
        await Assert.That(reader.GetInt32(0)).IsEqualTo(1);
        await Assert.That(reader.GetString(1)).IsEqualTo("Case-safe Whirlwind Slash");
    }

    [Test]
    public async Task RecordCompiler_UpdatesConceptualLocalizationFieldWithoutPhysicalColumn()
    {
        using var workspace = TestWorkspace.Create();
        var recordsDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "records");
        Directory.CreateDirectory(recordsDirectory);
        File.WriteAllText(Path.Combine(recordsDirectory, "conceptual-localization.json"), ContentStudioJson.Serialize(new RecordDefinition
        {
            Key = "record.test-conceptual-localization",
            DisplayName = "Localized skill alias",
            Mode = RecordChangeMode.Modify,
            Table = "SKILLS",
            SourceId = 200,
            Id = 200,
            Localizations = new Dictionary<string, Dictionary<string, string>>
            {
                ["ALIAS"] = new() { ["EN_US"] = "Spinning Slash" }
            }
        }));

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = CompactConnectionFactory.OpenReadOnly(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl_name, tbl_column_name, en_us, COUNT(*) FROM localized_texts WHERE tbl_name = 'skills' COLLATE NOCASE AND tbl_column_name = 'alias' COLLATE NOCASE AND idx = 200;";
        using var reader = command.ExecuteReader();

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("skills");
        await Assert.That(reader.GetString(1)).IsEqualTo("alias");
        await Assert.That(reader.GetString(2)).IsEqualTo("Spinning Slash");
        await Assert.That(reader.GetInt32(3)).IsEqualTo(1);
    }

    [Test]
    public async Task RecordCompiler_CanonicalizesConceptualFieldFromAnotherRecord()
    {
        using var workspace = TestWorkspace.Create();
        var saved = new RecordScaffoldService().Save(new RecordDraftRequest
        {
            ProjectPath = workspace.ProjectPath,
            BaselinePath = workspace.BaselinePath,
            Table = "GAME_RULE_SETS",
            SourceId = 8,
            Mode = RecordChangeMode.Modify,
            DisplayName = "Rule Eight",
            Values = new Dictionary<string, string?> { ["CODE"] = "eight" },
            Localizations = new Dictionary<string, Dictionary<string, string>>
            {
                ["NAME"] = new() { ["EN_US"] = "Rule Eight" }
            }
        });
        var definition = ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(saved.Path), saved.Path);

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        using var connection = CompactConnectionFactory.OpenReadOnly(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl_column_name, en_us, COUNT(*) FROM localized_texts WHERE tbl_name = 'game_rule_sets' COLLATE NOCASE AND tbl_column_name = 'name' COLLATE NOCASE AND idx = 8;";
        using var reader = command.ExecuteReader();

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(definition.Localizations.Keys.Single()).IsEqualTo("name");
        await Assert.That(reader.GetString(0)).IsEqualTo("name");
        await Assert.That(reader.GetString(1)).IsEqualTo("Rule Eight");
        await Assert.That(reader.GetInt32(2)).IsEqualTo(1);
    }

    [Test]
    public async Task Deployment_MatchingSchemaPublishesArtifactAndCreatesBackup()
    {
        using var workspace = TestWorkspace.Create();
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var targetPath = Path.Combine(workspace.Root, "deploy", "compact.sqlite3");
        var backupPath = Path.Combine(workspace.Root, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(workspace.BaselinePath, targetPath);

        var manifest = new DeploymentService().Deploy(build.ArtifactPath, build.Manifest.ArtifactSha256, "test", new DeploymentTarget
        {
            Path = targetPath,
            BackupDirectory = backupPath
        }, workspace.OutputPath);

        await Assert.That(FileHashService.CalculateSha256(targetPath)).IsEqualTo(FileHashService.CalculateSha256(build.ArtifactPath));
        await Assert.That(manifest.BackupPath).IsNotNull();
        await Assert.That(File.Exists(manifest.BackupPath!)).IsTrue();
    }

    [Test]
    public async Task Deployment_RejectsArtifactReplacedAfterBuildReview()
    {
        using var workspace = TestWorkspace.Create();
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var targetPath = Path.Combine(workspace.Root, "deploy", "compact.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(workspace.BaselinePath, targetPath);
        var originalTargetHash = FileHashService.CalculateSha256(targetPath);
        File.Copy(workspace.BaselinePath, build.ArtifactPath, true);
        var target = new DeploymentTarget { Path = targetPath, BackupDirectory = Path.Combine(workspace.Root, "backups") };

        var dryRunException = Assert.Throws<ContentStudioException>(() => new DeploymentService().ValidateReviewedArtifact(
            build.ArtifactPath,
            build.Manifest.ArtifactSha256,
            target));

        var exception = Assert.Throws<ContentStudioException>(() => new DeploymentService().Deploy(
            build.ArtifactPath,
            build.Manifest.ArtifactSha256,
            "test",
            target,
            workspace.OutputPath));

        await Assert.That(dryRunException!.Message).Contains("changed after it was reviewed");
        await Assert.That(exception!.Message).Contains("changed after it was reviewed");
        await Assert.That(FileHashService.CalculateSha256(targetPath)).IsEqualTo(originalTargetHash);
    }

    [Test]
    public async Task Deployment_RestoresTargetWhenManifestPublicationFails()
    {
        using var workspace = TestWorkspace.Create();
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var targetPath = Path.Combine(workspace.Root, "deploy", "compact.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(workspace.BaselinePath, targetPath);
        var originalTargetHash = FileHashService.CalculateSha256(targetPath);
        var service = new DeploymentService((index, _) =>
        {
            if (index == 1) throw new IOException("Simulated deployment-manifest write failure.");
        });

        var exception = Assert.Throws<ContentStudioException>(() => service.Deploy(
            build.ArtifactPath,
            build.Manifest.ArtifactSha256,
            "test",
            new DeploymentTarget { Path = targetPath, BackupDirectory = Path.Combine(workspace.Root, "backups") },
            workspace.OutputPath));

        await Assert.That(exception!.Message).Contains("previous target state was restored");
        await Assert.That(FileHashService.CalculateSha256(targetPath)).IsEqualTo(originalTargetHash);
        await Assert.That(Directory.GetFiles(workspace.OutputPath, "deployment-test-*.json")).IsEmpty();
    }

    [Test]
    public async Task Deployment_DifferentSchemaIsRejectedWithoutChangingTarget()
    {
        using var workspace = TestWorkspace.Create();
        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var targetPath = Path.Combine(workspace.Root, "deploy", "server-compact.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(workspace.BaselinePath, targetPath);
        using (var connection = new SqliteConnection($"Data Source={targetPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE server_only_runtime_data (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }
        var originalHash = FileHashService.CalculateSha256(targetPath);

        var exception = Assert.Throws<ContentStudioException>(() => new DeploymentService().Deploy(build.ArtifactPath, build.Manifest.ArtifactSha256, "server", new DeploymentTarget
        {
            Path = targetPath,
            BackupDirectory = Path.Combine(workspace.Root, "backups")
        }, workspace.OutputPath));

        await Assert.That(exception!.Message).Contains("does not match the deployment target");
        await Assert.That(FileHashService.CalculateSha256(targetPath)).IsEqualTo(originalHash);
    }

    [Test]
    public async Task Build_ExecutesArtifactAssertions()
    {
        using var workspace = TestWorkspace.Create();
        var assertionDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "assertions");
        Directory.CreateDirectory(assertionDirectory);
        File.WriteAllText(Path.Combine(assertionDirectory, "skill-count.json"), ContentStudioJson.Serialize(new ContentAssertionDefinition
        {
            Key = "assertion.test.skill-count",
            Description = "The compiled artifact contains the source skill and its private recipe clone.",
            Query = "SELECT COUNT(*) FROM skills WHERE id IN (200, 9400000);",
            Expected = "2"
        }));

        var build = new BuildService().Build(workspace.CreateBuildRequest());

        await Assert.That(build.Manifest.AssertionCount).IsEqualTo(1);
        await Assert.That(build.Manifest.Validation.Issues.Any(issue => issue.Code == "artifact.assertion" && issue.Entity == "assertion.test.skill-count")).IsTrue();
    }

    [Test]
    public async Task Build_AuditSqlKeepsAssertionTextInsideItsComment()
    {
        using var workspace = TestWorkspace.Create();
        var assertionDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "assertions");
        Directory.CreateDirectory(assertionDirectory);
        File.WriteAllText(Path.Combine(assertionDirectory, "comment-safety.json"), ContentStudioJson.Serialize(new ContentAssertionDefinition
        {
            Key = "assertion.test.comment-safety",
            Description = "Safe check\nDELETE FROM buffs;",
            Query = "SELECT CAST(X'310A44524F50205441424C45206974656D733B202D2D' AS TEXT);",
            Expected = "1\nDROP TABLE items; --"
        }));

        var build = new BuildService().Build(workspace.CreateBuildRequest());
        var auditSql = File.ReadAllText(build.AuditSqlPath);
        using var connection = CompactConnectionFactory.OpenReadWrite(build.ArtifactPath);
        using var command = connection.CreateCommand();
        command.CommandText = auditSql;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        command.Parameters.AddWithValue("@name", "items");
        var itemsExist = Convert.ToInt32(command.ExecuteScalar()) == 1;
        command.Parameters["@name"].Value = "buffs";
        var buffsExist = Convert.ToInt32(command.ExecuteScalar()) == 1;

        await Assert.That(auditSql.Split('\n').Any(line => line.TrimStart().StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(auditSql.Split('\n').Any(line => line.TrimStart().StartsWith("DELETE FROM", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(itemsExist).IsTrue();
        await Assert.That(buffsExist).IsTrue();
    }

    [Test]
    public async Task DeleteAssertion_RemovesReleaseCheckWithoutRetiringIds()
    {
        using var workspace = TestWorkspace.Create();
        var assertionDirectory = Path.Combine(Path.GetDirectoryName(workspace.ProjectPath)!, "assertions");
        Directory.CreateDirectory(assertionDirectory);
        var path = Path.Combine(assertionDirectory, "temporary.json");
        File.WriteAllText(path, ContentStudioJson.Serialize(new ContentAssertionDefinition
        {
            Key = "assertion.test.temporary",
            Description = "Temporary release check.",
            Query = "SELECT 1;",
            Expected = "1"
        }));

        var preview = new ChangeDeletionService().Preview(workspace.ProjectPath, path);
        var result = new ChangeDeletionService().Delete(workspace.ProjectPath, path, preview.Version);

        await Assert.That(preview.CanDelete).IsTrue();
        await Assert.That(result.Type).IsEqualTo("Release check");
        await Assert.That(result.RetiredIdCount).IsEqualTo(0);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task ManifestSave_RejectsSilentOverwriteAfterExternalEdit()
    {
        using var workspace = TestWorkspace.Create();
        var manifests = new ManifestService();
        var path = manifests.FindByKey(workspace.ProjectPath, "recipe.test-recipe");
        var opened = manifests.ReadSnapshot(path);
        var externallyEdited = opened.Contents.Replace("Test Recipe", "Agent Updated Recipe", StringComparison.Ordinal);
        manifests.Save(path, externallyEdited);

        var conflictDetected = false;
        try
        {
            manifests.Save(path, opened.Contents, opened.Version);
        }
        catch (ContentStudioException exception)
        {
            conflictDetected = exception.Message.Contains("updated outside this editor", StringComparison.Ordinal);
        }

        await Assert.That(conflictDetected).IsTrue();
        await Assert.That(manifests.Read(path)).Contains("Agent Updated Recipe");
    }

    [Test]
    public async Task DesignerReferences_ResolveAndSearchByFriendlyNames()
    {
        using var workspace = TestWorkspace.Create();
        var references = new DesignerReferenceService();

        var item = references.Resolve(workspace.BaselinePath, workspace.ProjectPath, "items", 10);
        var workbenches = references.Search(workspace.BaselinePath, workspace.ProjectPath, "doodad_almighties", "alchemy");
        var ability = references.Resolve(workspace.BaselinePath, workspace.ProjectPath, "abilities", 1);

        await Assert.That(item!.Name).IsEqualTo("Moonlight Archeum Dust");
        await Assert.That(workbenches.Any(option => option.Name == "Alchemy Workbench")).IsTrue();
        await Assert.That(ability!.Name).IsEqualTo("Battlerage");
        await Assert.That(CatalogRecordService.FriendlyTableName("actability_categories")).IsEqualTo("Crafting proficiency");
    }

    [Test]
    public async Task DesignerPlanNaming_RepeatedCopiesGetFriendlySequence()
    {
        using var workspace = TestWorkspace.Create();
        var projectDirectory = Path.GetDirectoryName(workspace.ProjectPath)!;
        var recipeDirectory = Path.Combine(projectDirectory, "recipes");
        var workbenchDirectory = Path.Combine(projectDirectory, "workbenches");

        File.WriteAllText(Path.Combine(recipeDirectory, "custom-lumber.json"), ContentStudioJson.Serialize(new RecipeDefinition
        {
            Key = "recipe.custom-lumber",
            Names = new Dictionary<string, string> { ["en_us"] = "Custom Lumber" }
        }));
        File.WriteAllText(Path.Combine(recipeDirectory, "custom-lumber-2.json"), ContentStudioJson.Serialize(new RecipeDefinition
        {
            Key = "recipe.custom-lumber-2",
            Names = new Dictionary<string, string> { ["en_us"] = "Custom Lumber 2" }
        }));
        File.WriteAllText(Path.Combine(workbenchDirectory, "custom-alchemy-workbench.json"), ContentStudioJson.Serialize(new WorkbenchDefinition
        {
            Key = "workbench.custom-alchemy-workbench",
            Names = new Dictionary<string, string> { ["en_us"] = "Custom Alchemy Workbench" }
        }));

        var naming = new DesignerPlanNamingService();
        var recipe = naming.SuggestRecipeCopy(workspace.ProjectPath, "Lumber");
        var workbench = naming.SuggestWorkbenchCopy(workspace.ProjectPath, "Alchemy Workbench");

        await Assert.That(recipe.Name).IsEqualTo("Custom Lumber 3");
        await Assert.That(recipe.Key).IsEqualTo("recipe.custom-lumber-3");
        await Assert.That(workbench.Name).IsEqualTo("Custom Alchemy Workbench 2");
        await Assert.That(workbench.Key).IsEqualTo("workbench.custom-alchemy-workbench-2");
    }

    private static RecordDraftRequest CreateRecordRequest(TestWorkspace workspace, CatalogRecord record, List<RecordLinkedDraft> linkedRecords) => new()
    {
        ProjectPath = workspace.ProjectPath,
        BaselinePath = workspace.BaselinePath,
        Table = record.Table,
        SourceId = record.Id,
        Mode = RecordChangeMode.Modify,
        DisplayName = record.Name,
        Values = record.Fields
            .Where(field => !field.IsIdentity && field.IsEditable)
            .ToDictionary(field => field.Name, field => field.Value),
        Localizations = record.Localizations.ToDictionary(field => field.Field, field => field.Values),
        Children = record.RelatedSections.SelectMany(section => section.Rows.Select(row => new RecordChildDraft
        {
            Table = section.Table,
            OwnerColumn = section.OwnerColumn,
            SourceId = row.Id,
            Values = row.Fields
                .Where(field => !field.IsIdentity && field.IsEditable)
                .ToDictionary(field => field.Name, field => field.Value)
        })).ToList(),
        LinkedRecords = linkedRecords
    };
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
            CREATE TABLE items (id INTEGER, name TEXT, category_id INTEGER, price INTEGER, description TEXT, level INTEGER, level_requirement INTEGER, gradable NUM, fixed_grade INTEGER, buff_id INTEGER, use_skill_id INTEGER);
            CREATE TABLE crafts (id INTEGER, title TEXT, cast_delay INTEGER, tool_id INTEGER, skill_id INTEGER, wi_id INTEGER, desc TEXT, milestone_id INTEGER, req_doodad_id INTEGER, need_bind TEXT, ac_id INTEGER, actability_limit INTEGER, show_upper_crafts TEXT, recommend_level INTEGER, visible_order INTEGER, translate TEXT);
            CREATE TABLE skills (id INTEGER, name TEXT, desc TEXT, web_desc TEXT, ability_id INTEGER, ability_level INTEGER, mana_cost INTEGER, cooldown_time INTEGER, consume_lp INTEGER, casting_time INTEGER, show NUM, icon_id INTEGER, max_range INTEGER, skill_controller_id INTEGER);
            CREATE TABLE skill_effects (id INTEGER, skill_id INTEGER, effect_id INTEGER, start_level INTEGER, end_level INTEGER, chance INTEGER);
            CREATE TABLE effects (id INTEGER, actual_type TEXT, actual_id INTEGER);
            CREATE TABLE buff_effects (id INTEGER, buff_id INTEGER, chance INTEGER, stack INTEGER, ab_level INTEGER);
            CREATE TABLE buffs (id INTEGER, duration INTEGER, level_duration INTEGER, init_min_charge INTEGER, init_max_charge INTEGER, max_stack INTEGER);
            CREATE TABLE unit_modifiers (id INTEGER, owner_id INTEGER, owner_type TEXT, unit_attribute_id INTEGER, unit_modifier_type_id INTEGER, value INTEGER, linear_level_bonus INTEGER);
            CREATE TABLE item_weapons (id INTEGER, item_id INTEGER, holdable_id INTEGER, mod_set_id INTEGER, eiset_id INTEGER, base_enchantable NUM, repairable NUM, durability_multiplier INTEGER, recharge_buff_id INTEGER);
            CREATE TABLE item_armors (id INTEGER, item_id INTEGER, type_id INTEGER, slot_type_id INTEGER, mod_set_id INTEGER, eiset_id INTEGER, base_enchantable NUM, repairable NUM, durability_multiplier INTEGER, recharge_buff_id INTEGER);
            CREATE TABLE item_accessories (id INTEGER, item_id INTEGER, type_id INTEGER, slot_type_id INTEGER, mod_set_id INTEGER, eiset_id INTEGER, repairable NUM, durability_multiplier INTEGER, recharge_buff_id INTEGER);
            CREATE TABLE holdables (id INTEGER, name TEXT, code TEXT, slot_type_id INTEGER, speed INTEGER, damage_scale INTEGER, max_range INTEGER, item_proc_id INTEGER);
            CREATE TABLE wearables (id INTEGER, armor_type_id INTEGER, slot_type_id INTEGER, armor_bp INTEGER, magic_resistance_bp INTEGER);
            CREATE TABLE equip_item_attr_modifiers (id INTEGER, str_weight INTEGER, dex_weight INTEGER, sta_weight INTEGER, int_weight INTEGER, spi_weight INTEGER);
            CREATE TABLE item_grades (id INTEGER, name TEXT, grade_order INTEGER, stat_multiplier INTEGER);
            CREATE TABLE game_rule_sets (id INTEGER, code TEXT);
            CREATE TABLE equip_item_sets (id INTEGER, name TEXT, description TEXT);
            CREATE TABLE equip_item_set_bonuses (id INTEGER, equip_item_set_id INTEGER, num_pieces INTEGER, buff_id INTEGER, proc_id INTEGER);
            CREATE TABLE item_procs (id INTEGER, name TEXT, description TEXT);
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
            INSERT INTO items VALUES
              (10, 'Input', 1, 1, '', 1, 0, 0, 0, 0, 0),
              (11, 'Output', 1, 1, '', 1, 0, 0, 0, 0, 0),
              (12, 'Wave Scepter', 75, 36300, 'A scepter from the Delphinad Wave set.', 50, 50, 1, 0, 0, 0);
            INSERT INTO skills VALUES (200, 'Whirlwind Slash', 'Spin and attack nearby enemies.', 'Spin and attack nearby enemies.', 1, 10, 15, 12000, 5, 1000, 't', 42, 4000, '--- :null');
            INSERT INTO skill_effects VALUES (201, 200, 1, 10, 19, 100);
            INSERT INTO effects VALUES (1, 'BuffEffect', 2);
            INSERT INTO buff_effects VALUES (2, 3, 100, 1, 0);
            INSERT INTO buffs VALUES (3, 0, 0, 561, 561, 1);
            INSERT INTO buffs VALUES (4, 0, 0, 0, 0, 1);
            INSERT INTO unit_modifiers VALUES (4, 3, 'Buff', 8, 0, 700, 0);
            INSERT INTO unit_modifiers VALUES (5, 4, 'Buff', 5, 0, 12, 0);
            INSERT INTO item_weapons VALUES (600, 12, 20, 30, 40, 1, 1, 100, 0);
            INSERT INTO holdables VALUES (20, 'Scepter', '1h_staff', 2, 1000, 100, 4000, 50);
            INSERT INTO equip_item_attr_modifiers VALUES (30, 0, 0, 0, 2, 1);
            INSERT INTO item_grades VALUES (0, 'Basic', 1, 100);
            INSERT INTO game_rule_sets VALUES (7, 'seven'), (8, 'eight');
            INSERT INTO equip_item_sets VALUES (40, 'Wave Set', 'Equipment favored by Wave spellcasters.');
            INSERT INTO equip_item_set_bonuses VALUES (41, 40, 2, 4, 0);
            INSERT INTO item_procs VALUES (50, 'Wave Burst', 'Occasionally releases a burst of magic.');
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
            INSERT INTO localized_texts (id, tbl_name, tbl_column_name, idx, ko, en_us, fr) VALUES
              (504, 'skills', 'name', 200, '소용돌이 베기', 'Whirlwind Slash', 'Tourbillon tranchant');
            INSERT INTO localized_texts (id, tbl_name, tbl_column_name, idx, en_us) VALUES
              (505, 'skills', 'web_desc', 200, 'Spin and attack nearby enemies.'),
              (506, 'buffs', 'name', 3, 'Protective Ward'),
              (507, 'items', 'name', 12, 'Delphinad Wave Scepter'),
              (508, 'buffs', 'name', 4, 'Wave Wisdom'),
              (509, 'item_procs', 'name', 50, 'Wave Burst'),
              (510, 'skills', 'alias', 200, 'Whirling Cut'),
              (511, 'game_rule_sets', 'name', 7, 'Rule Seven');
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
            TableCount = 29,
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
                ["skills"] = new IdRange { Start = 9_400_000, End = 9_400_010 },
                ["skill_effects"] = new IdRange { Start = 9_500_000, End = 9_500_010 },
                ["localized_texts"] = new IdRange { Start = 9_600_000, End = 9_600_010 },
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
