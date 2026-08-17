using AAEmu.Game.Models.Game.Quests;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

/// <summary>
/// Fixtures mirror real ui_data type-5 blobs captured from a live 1.2 client
/// (CRLF line endings, 4-space indents, "\u0001" header line, and both the
/// indented and the compact single-line list forms the client serializer emits).
/// </summary>
public class QuestTrackerUiDataTests
{
    private static string Blob(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static readonly string IndentedBlob = Blob(
        "\"\u0001\"",
        "roadmap_option ( size 1, visible true, npcShow true )",
        "quest_notifier_list",
        "    listOpen false",
        "    openHeight 300",
        "    questList ( 4407, 6518, 4617, 4580 )",
        "    decalStates ( 4407, 0, 0, 0, 0, 0, 0, 0, 0, 0 )",
        "quest_context_state_values",
        "    newfolderStates",
        "        70 open",
        "        6 open",
        "    checkBoxStates",
        "        1660 false",
        "        8000004 false",
        "        4617 true",
        "version 1",
        "worldmap_option expansionLevel 1");

    private static readonly string InlineBlob = Blob(
        "\"\u0001\"",
        "roadmap_option ( size 3, visible true, npcShow true )",
        "quest_notifier_list",
        "    listOpen true",
        "    openHeight 300",
        "    questList ( 259, 5307 )",
        "quest_context_state_values",
        "    newfolderStates ( 70 close, 114 open )",
        "    checkBoxStates ( 5307 true, 259 false )",
        "version 1",
        "worldmap_option expansionLevel 2");

    [Test]
    public async Task AddsCheckedEntriesForUntoggledActiveQuests_IndentedList()
    {
        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(IndentedBlob, [1660u, 4617u, 4800u, 4700u]);

        var expected = Blob(
            "\"\u0001\"",
            "roadmap_option ( size 1, visible true, npcShow true )",
            "quest_notifier_list",
            "    listOpen false",
            "    openHeight 300",
            "    questList ( 4407, 6518, 4617, 4580 )",
            "    decalStates ( 4407, 0, 0, 0, 0, 0, 0, 0, 0, 0 )",
            "quest_context_state_values",
            "    newfolderStates",
            "        70 open",
            "        6 open",
            "    checkBoxStates",
            "        1660 false",
            "        8000004 false",
            "        4617 true",
            "        4700 true",
            "        4800 true",
            "version 1",
            "worldmap_option expansionLevel 1");

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task NeverOverridesExplicitEntries()
    {
        // 1660 was deliberately unchecked, 4617 checked; both stay untouched.
        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(IndentedBlob, [1660u, 4617u, 8000004u]);

        await Assert.That(result).IsEqualTo(IndentedBlob);
    }

    [Test]
    public async Task AddsCheckedEntriesForUntoggledActiveQuests_InlineList()
    {
        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(InlineBlob, [5307u, 259u, 600u]);

        var expected = InlineBlob.Replace(
            "    checkBoxStates ( 5307 true, 259 false )",
            "    checkBoxStates ( 5307 true, 259 false, 600 true )");
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task CreatesInlineListWhenSectionHasNoCheckBoxStates()
    {
        var blob = Blob(
            "\"\u0001\"",
            "quest_context_state_values",
            "    newfolderStates ( 70 close )",
            "version 1");

        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(blob, [42u]);

        var expected = Blob(
            "\"\u0001\"",
            "quest_context_state_values",
            "    newfolderStates ( 70 close )",
            "    checkBoxStates ( 42 true )",
            "version 1");
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task AddsEntriesToEmptyInlineList()
    {
        var blob = Blob(
            "quest_context_state_values",
            "    checkBoxStates (  )",
            "version 1");

        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(blob, [42u, 7u]);

        var expected = Blob(
            "quest_context_state_values",
            "    checkBoxStates ( 7 true, 42 true )",
            "version 1");
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ReturnsUnchangedWhenBlobHasNoQuestSection()
    {
        var blob = Blob(
            "\"\u0001\"",
            "roadmap_option ( size 1, visible true, npcShow true )",
            "version 1");

        var result = QuestTrackerUiData.EnsureActiveQuestsChecked(blob, [42u]);

        await Assert.That(result).IsEqualTo(blob);
    }

    [Test]
    public async Task ReturnsUnchangedForEmptyInputs()
    {
        await Assert.That(QuestTrackerUiData.EnsureActiveQuestsChecked("", [42u])).IsEqualTo("");
        await Assert.That(QuestTrackerUiData.EnsureActiveQuestsChecked(null, [42u])).IsNull();
        await Assert.That(QuestTrackerUiData.EnsureActiveQuestsChecked(IndentedBlob, [])).IsEqualTo(IndentedBlob);
    }

    [Test]
    public async Task IsIdempotent()
    {
        var once = QuestTrackerUiData.EnsureActiveQuestsChecked(IndentedBlob, [4700u]);
        var twice = QuestTrackerUiData.EnsureActiveQuestsChecked(once, [4700u]);

        await Assert.That(twice).IsEqualTo(once);
    }
}
