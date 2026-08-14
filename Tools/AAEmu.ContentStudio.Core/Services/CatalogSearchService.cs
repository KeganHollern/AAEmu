using System.Globalization;
using System.Text;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class CatalogSearchService
{
    private static readonly Dictionary<string, (string Kind, string Label, string Inspect)> s_knownKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["items"] = ("item", "Item", "item"),
        ["item_categories"] = ("item", "Item category", "generic"),
        ["crafts"] = ("recipe", "Recipe", "recipe"),
        ["doodad_almighties"] = ("doodad", "World object", "generic"),
        ["npcs"] = ("npc", "NPC", "generic"),
        ["skills"] = ("skill", "Skill", "generic"),
        ["buffs"] = ("buff", "Buff", "generic"),
        ["quest_contexts"] = ("quest", "Quest", "generic"),
        ["quest_names"] = ("quest", "Quest name", "generic"),
        ["quest_categories"] = ("quest", "Quest category", "generic"),
        ["achievements"] = ("achievement", "Achievement", "generic"),
        ["appellations"] = ("achievement", "Title", "generic"),
        ["zones"] = ("world", "Zone", "generic"),
        ["sub_zones"] = ("world", "Sub-zone", "generic"),
        ["return_points"] = ("world", "Return point", "generic"),
        ["housings"] = ("world", "Housing", "generic"),
        ["slaves"] = ("world", "Vehicle", "generic"),
        ["abilities"] = ("ability", "Ability / skillset", "ability"),
        ["actability_categories"] = ("skill", "Proficiency", "generic"),
        ["ui_texts"] = ("other", "Interface text", "generic")
    };

    public CatalogSearchResponse SearchEverything(string compactPath, string query, string language = "en_us", int limit = 80)
    {
        CompactCatalogService.ValidateLanguageColumn(language);
        var search = query.Trim();
        if (search.Length == 0)
        {
            return new CatalogSearchResponse { Query = search };
        }

        limit = Math.Clamp(limit, 1, 200);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        var results = new Dictionary<(string Table, uint Id), CatalogSearchResult>();
        AddAbilityMatches(results, search);
        var labels = ReadLabels(connection, language);
        var candidates = ReadTextMatches(connection, search, language);

        foreach (var candidate in candidates)
        {
            var label = labels.GetValueOrDefault((candidate.Table, candidate.Id));
            var name = label?.Text ?? Condense(candidate.Text, 100);
            var score = candidate.ScoreOverride > 0 ? candidate.ScoreOverride : ScoreTextMatch(search, candidate.Text, candidate.Field);
            var context = candidate.Field is "name" or "title" && TextEquals(name, candidate.Text)
                ? null
                : $"Matches {FriendlyName(candidate.Field)}: {Snippet(candidate.Text, search)}";
            AddOrImprove(results, CreateResult(candidate.Table, candidate.Id, name, candidate.Field, context, score));
        }

        var usedFuzzyMatching = false;
        if (results.Count < Math.Min(12, limit) && search.Any(char.IsLetter))
        {
            foreach (var label in labels.Values)
            {
                var fuzzyScore = ScoreFuzzyMatch(search, label.Text);
                if (fuzzyScore <= 0)
                {
                    continue;
                }

                usedFuzzyMatching = true;
                AddOrImprove(results, CreateResult(
                    label.Table,
                    label.Id,
                    label.Text,
                    label.Field,
                    $"Close name match for “{search}”",
                    fuzzyScore));
            }
        }

        ExpandRecipesForMatchingItems(connection, labels, results);
        ExpandWorkbenchesForMatchingRecipes(connection, labels, results);
        ClassifyWorkbenches(connection, results.Values);

        var ordered = results.Values
            .OrderByDescending(result => result.Score)
            .ThenBy(result => KindOrder(result.Kind))
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id)
            .ToList();

        return new CatalogSearchResponse
        {
            Query = search,
            UsedFuzzyMatching = usedFuzzyMatching,
            Results = SelectDiversified(ordered, limit)
        };
    }

    private static void AddAbilityMatches(IDictionary<(string Table, uint Id), CatalogSearchResult> results, string search)
    {
        var normalized = Normalize(search);
        var asksForAll = normalized is "ability" or "abilities" or "skillset" or "skillsets" or "class" or "classes";
        foreach (var (id, ability) in CatalogRecordService.Abilities)
        {
            var name = Normalize(ability.Name);
            if (!asksForAll && !name.Contains(normalized, StringComparison.Ordinal) && !normalized.Contains(name, StringComparison.Ordinal))
            {
                continue;
            }

            AddOrImprove(results, new CatalogSearchResult
            {
                Id = id,
                Name = ability.Name,
                Kind = "ability",
                KindLabel = "Ability / skillset",
                Table = "abilities",
                Field = "name",
                Context = ability.Description,
                Score = asksForAll ? 1_025 : name == normalized ? 1_150 : 1_075,
                InspectKind = "ability"
            });
        }
    }

    private static Dictionary<(string Table, uint Id), SearchLabel> ReadLabels(SqliteConnection connection, string language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT tbl_name, tbl_column_name, idx, {BaselineVerifier.QuoteIdentifier(language)}
              FROM localized_texts
             WHERE tbl_column_name IN ('name', 'title')
               AND {BaselineVerifier.QuoteIdentifier(language)} IS NOT NULL
               AND LENGTH(TRIM({BaselineVerifier.QuoteIdentifier(language)})) > 0
             ORDER BY CASE tbl_column_name WHEN 'name' THEN 0 ELSE 1 END, id;
            """;
        using var reader = command.ExecuteReader();
        var labels = new Dictionary<(string Table, uint Id), SearchLabel>();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var field = reader.GetString(1);
            var id = Convert.ToUInt32(reader.GetInt64(2));
            labels.TryAdd((table, id), new SearchLabel(table, id, field, reader.GetString(3)));
        }

        return labels;
    }

    private static List<SearchTextMatch> ReadTextMatches(SqliteConnection connection, string search, string language)
    {
        if (uint.TryParse(search, out var exactId))
        {
            return ReadIdMatches(connection, exactId, language);
        }

        var tokens = Normalize(search).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(8).ToArray();
        if (tokens.Length == 0)
        {
            return [];
        }
        using var command = connection.CreateCommand();
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        var predicates = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            predicates.Add($"LOWER({languageColumn}) LIKE @token{index} ESCAPE '!'");
            command.Parameters.AddWithValue($"@token{index}", $"%{EscapeLike(tokens[index])}%");
        }

        command.CommandText = $"""
            SELECT tbl_name, tbl_column_name, idx, {languageColumn}
              FROM localized_texts
             WHERE {languageColumn} IS NOT NULL
               AND LENGTH(TRIM({languageColumn})) > 0
               AND ({string.Join(" AND ", predicates)})
             ORDER BY CASE
                        WHEN LOWER({languageColumn}) = @normalized THEN 0
                        WHEN LOWER({languageColumn}) LIKE @prefix ESCAPE '!' THEN 1
                        WHEN tbl_column_name IN ('name', 'title') THEN 2
                        ELSE 3
                      END,
                      LENGTH({languageColumn}),
                      tbl_name,
                      idx
             LIMIT 500;
            """;
        command.Parameters.AddWithValue("@normalized", search.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("@prefix", $"{EscapeLike(search.Trim().ToLowerInvariant())}%");
        using var reader = command.ExecuteReader();
        var matches = new List<SearchTextMatch>();
        while (reader.Read())
        {
            matches.Add(new SearchTextMatch(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToUInt32(reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return matches;
    }

    private static List<SearchTextMatch> ReadIdMatches(SqliteConnection connection, uint exactId, string language)
    {
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT tbl_name, tbl_column_name, idx, {languageColumn}
              FROM localized_texts
             WHERE idx = @id
               AND {languageColumn} IS NOT NULL
               AND LENGTH(TRIM({languageColumn})) > 0
             ORDER BY CASE WHEN tbl_column_name IN ('name', 'title') THEN 0 ELSE 1 END, tbl_name
             LIMIT 200;
            """;
        command.Parameters.AddWithValue("@id", exactId);
        using var reader = command.ExecuteReader();
        var matches = new List<SearchTextMatch>();
        while (reader.Read())
        {
            matches.Add(new SearchTextMatch(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToUInt32(reader.GetInt64(2)),
                reader.GetString(3),
                1_100));
        }
        return matches;
    }

    private static void ExpandRecipesForMatchingItems(
        SqliteConnection connection,
        IReadOnlyDictionary<(string Table, uint Id), SearchLabel> labels,
        IDictionary<(string Table, uint Id), CatalogSearchResult> results)
    {
        var itemResults = results.Values.Where(result => result.Table == "items").OrderByDescending(result => result.Score).Take(24).ToList();
        if (itemResults.Count == 0)
        {
            return;
        }

        var itemNames = itemResults.ToDictionary(result => result.Id, result => result.Name);
        using var command = connection.CreateCommand();
        var itemParameters = AddIdParameters(command, itemNames.Keys, "item");
        command.CommandText = $"""
            SELECT c.id, cm.item_id, cm.amount, 'material'
              FROM craft_materials cm
              JOIN crafts c ON c.id = cm.craft_id
             WHERE cm.item_id IN ({itemParameters})
            UNION ALL
            SELECT c.id, cp.item_id, cp.amount, 'product'
              FROM craft_products cp
              JOIN crafts c ON c.id = cp.craft_id
             WHERE cp.item_id IN ({itemParameters})
             LIMIT 160;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recipeId = Convert.ToUInt32(reader.GetInt64(0));
            var itemId = Convert.ToUInt32(reader.GetInt64(1));
            var amount = reader.GetInt32(2);
            var relation = reader.GetString(3);
            var itemResult = itemResults.First(result => result.Id == itemId);
            var recipeName = labels.GetValueOrDefault(("crafts", recipeId))?.Text ?? "Unnamed recipe";
            var verb = relation == "material" ? "Uses" : "Produces";
            var result = CreateResult("crafts", recipeId, recipeName, relation, $"{verb} {amount} × {itemNames[itemId]}", itemResult.Score - 35);
            result.RelatedTable = "items";
            result.RelatedId = itemId;
            AddOrImprove(results, result);
        }
    }

    private static void ExpandWorkbenchesForMatchingRecipes(
        SqliteConnection connection,
        IReadOnlyDictionary<(string Table, uint Id), SearchLabel> labels,
        IDictionary<(string Table, uint Id), CatalogSearchResult> results)
    {
        var recipeResults = results.Values.Where(result => result.Table == "crafts").OrderByDescending(result => result.Score).Take(30).ToList();
        if (recipeResults.Count == 0)
        {
            return;
        }

        var recipeNames = recipeResults.ToDictionary(result => result.Id, result => result.Name);
        using var command = connection.CreateCommand();
        var recipeParameters = AddIdParameters(command, recipeNames.Keys, "recipe");
        command.CommandText = $"""
            SELECT DISTINCT d.id, cpc.craft_id
              FROM craft_pack_crafts cpc
              JOIN doodad_func_craft_packs payload ON payload.craft_pack_id = cpc.craft_pack_id
              JOIN doodad_funcs func
                ON func.actual_func_type = 'DoodadFuncCraftPack'
               AND func.actual_func_id = payload.id
              JOIN doodad_func_groups groups ON groups.id = func.doodad_func_group_id
              JOIN doodad_almighties d ON d.id = groups.doodad_almighty_id
             WHERE cpc.craft_id IN ({recipeParameters})
             LIMIT 120;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var doodadId = Convert.ToUInt32(reader.GetInt64(0));
            var recipeId = Convert.ToUInt32(reader.GetInt64(1));
            var recipeResult = recipeResults.First(result => result.Id == recipeId);
            var workbenchName = labels.GetValueOrDefault(("doodad_almighties", doodadId))?.Text ?? "Unnamed workbench";
            var result = CreateResult("doodad_almighties", doodadId, workbenchName, "craft_pack", $"Offers recipe: {recipeNames[recipeId]}", recipeResult.Score - 70);
            result.Kind = "workbench";
            result.KindLabel = "Workbench";
            result.InspectKind = "workbench";
            result.RelatedTable = "crafts";
            result.RelatedId = recipeId;
            AddOrImprove(results, result);
        }
    }

    private static void ClassifyWorkbenches(SqliteConnection connection, IEnumerable<CatalogSearchResult> results)
    {
        var doodads = results.Where(result => result.Table == "doodad_almighties").ToList();
        if (doodads.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        var parameters = AddIdParameters(command, doodads.Select(result => result.Id).Distinct(), "doodad");
        command.CommandText = $"""
            SELECT DISTINCT groups.doodad_almighty_id
              FROM doodad_func_groups groups
              JOIN doodad_funcs func ON func.doodad_func_group_id = groups.id
             WHERE func.actual_func_type = 'DoodadFuncCraftPack'
               AND groups.doodad_almighty_id IN ({parameters});
            """;
        using var reader = command.ExecuteReader();
        var workbenchIds = new HashSet<uint>();
        while (reader.Read())
        {
            workbenchIds.Add(Convert.ToUInt32(reader.GetInt64(0)));
        }

        foreach (var result in doodads.Where(result => workbenchIds.Contains(result.Id)))
        {
            result.Kind = "workbench";
            result.KindLabel = "Workbench";
            result.InspectKind = "workbench";
        }
    }

    private static string AddIdParameters(SqliteCommand command, IEnumerable<uint> ids, string prefix)
    {
        var names = new List<string>();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            var name = $"@{prefix}{index}";
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return string.Join(", ", names);
    }

    private static CatalogSearchResult CreateResult(string table, uint id, string name, string field, string? context, int score)
    {
        var kind = s_knownKinds.TryGetValue(table, out var known)
            ? known
            : (Kind: "other", Label: FriendlyName(table), Inspect: "generic");
        return new CatalogSearchResult
        {
            Id = id,
            Name = Condense(name, 120),
            Kind = kind.Kind,
            KindLabel = kind.Label,
            Table = table,
            Field = field,
            Context = context,
            Score = score,
            InspectKind = kind.Inspect
        };
    }

    private static void AddOrImprove(IDictionary<(string Table, uint Id), CatalogSearchResult> results, CatalogSearchResult candidate)
    {
        var key = (candidate.Table, candidate.Id);
        if (!results.TryGetValue(key, out var current) || candidate.Score > current.Score)
        {
            results[key] = candidate;
        }
    }

    private static int ScoreTextMatch(string search, string text, string field)
    {
        var normalizedSearch = Normalize(search);
        var normalizedText = Normalize(text);
        var score = normalizedText == normalizedSearch
            ? 1_000
            : normalizedText.StartsWith(normalizedSearch, StringComparison.Ordinal) ? 900
            : normalizedText.Contains(normalizedSearch, StringComparison.Ordinal) ? 800
            : 700;
        if (field is "name" or "title")
        {
            score += 50;
        }
        return score;
    }

    private static int ScoreFuzzyMatch(string search, string candidate)
    {
        var searchTokens = Normalize(search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateTokens = Normalize(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (searchTokens.Length == 0 || candidateTokens.Length == 0 || candidate.Length > 160)
        {
            return 0;
        }

        var totalDistance = 0;
        foreach (var searchToken in searchTokens)
        {
            var allowed = searchToken.Length <= 4 ? 1 : 2;
            var best = candidateTokens
                .Where(token => Math.Abs(token.Length - searchToken.Length) <= allowed && (token[0] == searchToken[0] || searchToken.Length <= 3))
                .Select(token => TokenDistance(searchToken, token, allowed))
                .DefaultIfEmpty(allowed + 1)
                .Min();
            if (best > allowed)
            {
                return 0;
            }
            totalDistance += best;
        }

        return totalDistance == 0 ? 0 : 640 - (totalDistance * 35) - Math.Abs(candidateTokens.Length - searchTokens.Length) * 5;
    }

    private static int TokenDistance(string left, string right, int stopAfter)
    {
        if (left.Length == right.Length)
        {
            var differences = Enumerable.Range(0, left.Length).Where(index => left[index] != right[index]).ToArray();
            if (differences.Length == 2 && differences[1] == differences[0] + 1 &&
                left[differences[0]] == right[differences[1]] && left[differences[1]] == right[differences[0]])
            {
                return 1;
            }
        }
        return EditDistance(left, right, stopAfter);
    }

    private static int EditDistance(string left, string right, int stopAfter)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            var rowMinimum = current[0];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1), substitution);
                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }
            if (rowMinimum > stopAfter)
            {
                return stopAfter + 1;
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static string EscapeLike(string value) => value.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal);

    private static string Snippet(string text, string search)
    {
        var condensed = Condense(text, 260);
        var index = condensed.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || condensed.Length <= 150)
        {
            return Condense(condensed, 150);
        }
        var start = Math.Max(0, index - 45);
        var length = Math.Min(condensed.Length - start, 145);
        return $"{(start > 0 ? "…" : string.Empty)}{condensed.Substring(start, length)}{(start + length < condensed.Length ? "…" : string.Empty)}";
    }

    private static string Condense(string value, int maximumLength)
    {
        var condensed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return condensed.Length <= maximumLength ? condensed : condensed[..(maximumLength - 1)] + "…";
    }

    private static string FriendlyName(string value)
    {
        var words = value.Replace('_', ' ').Trim();
        return words.Length == 0 ? "Other" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static bool TextEquals(string left, string right) => Normalize(left) == Normalize(right);

    private static int KindOrder(string kind) => kind switch
    {
        "ability" => 0,
        "skill" => 1,
        "buff" => 2,
        "item" => 3,
        "recipe" => 4,
        "workbench" => 5,
        "npc" => 6,
        "quest" => 7,
        "world" => 8,
        _ => 9
    };

    private static List<CatalogSearchResult> SelectDiversified(List<CatalogSearchResult> ordered, int limit)
    {
        if (ordered.Count <= limit)
        {
            return ordered;
        }

        var selected = new List<CatalogSearchResult>(limit);
        var selectedKeys = new HashSet<(string Table, uint Id)>();
        var tableCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableCap = Math.Clamp(limit / 8, 2, 8);
        foreach (var result in ordered)
        {
            if (tableCounts.GetValueOrDefault(result.Table) >= tableCap)
            {
                continue;
            }
            selected.Add(result);
            selectedKeys.Add((result.Table, result.Id));
            tableCounts[result.Table] = tableCounts.GetValueOrDefault(result.Table) + 1;
            if (selected.Count == limit)
            {
                return selected;
            }
        }

        foreach (var result in ordered.Where(result => !selectedKeys.Contains((result.Table, result.Id))))
        {
            selected.Add(result);
            if (selected.Count == limit)
            {
                break;
            }
        }
        return selected;
    }

    private sealed record SearchLabel(string Table, uint Id, string Field, string Text);
    private sealed record SearchTextMatch(string Table, string Field, uint Id, string Text, int ScoreOverride = 0);
}
