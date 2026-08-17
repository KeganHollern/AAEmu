namespace AAEmu.Game.Models.Game.Quests;

/// <summary>
/// Server-side fix-up of the client's saved quest-log UI state (ui_data type 5).
///
/// The client restores the journal checkboxes (and through them the quest
/// tracker) exclusively from the "quest_context_state_values → checkBoxStates"
/// entries of this blob; the per-quest isCheckSet field of the quest packets is
/// not consulted for that. When a quest is accepted the client only checks the
/// checkbox widget; a checkBoxStates entry is only ever written by a physical
/// click on the checkbox (synced via CSSaveUIDataPacket). As a result,
/// accepted-but-never-toggled quests came back unchecked after every relog
/// (aaemu-cluster issue #81).
///
/// EnsureActiveQuestsChecked synthesizes the expected default at serve time:
/// every active quest without an explicit checkBoxStates entry gets an
/// "<id> true" entry added to the copy sent to the client. Explicit entries
/// (true or false) are never modified, so deliberate unchecks keep persisting.
/// The stored option row itself remains client-authored; whenever the blob does
/// not look like the known format it is returned unmodified.
/// </summary>
public static class QuestTrackerUiData
{
    /// <summary>ui_data type under which the client saves quest-log/journal state.</summary>
    public const ushort UiDataType = 5;

    private const string SectionName = "quest_context_state_values";
    private const string ListName = "checkBoxStates";
    private const string LineBreak = "\r\n";

    /// <summary>
    /// Returns <paramref name="uiData"/> with a "&lt;id&gt; true" checkBoxStates entry
    /// added for every id in <paramref name="activeQuestTemplateIds"/> that has no
    /// explicit entry yet. Returns the input unchanged when there is nothing to add
    /// or the blob cannot be safely edited.
    /// </summary>
    public static string EnsureActiveQuestsChecked(string uiData, IEnumerable<uint> activeQuestTemplateIds)
    {
        if (string.IsNullOrEmpty(uiData))
            return uiData;

        // SortedSet for deterministic output order of synthesized entries
        var missing = new SortedSet<uint>(activeQuestTemplateIds);
        if (missing.Count == 0)
            return uiData;

        var lines = uiData.Split(LineBreak).ToList();

        // Locate the quest_context_state_values section header. If the client has
        // never saved journal state there is no known-good envelope to extend.
        var sectionIndex = lines.FindIndex(l => l.Trim() == SectionName);
        if (sectionIndex < 0)
            return uiData;

        var sectionIndent = IndentOf(lines[sectionIndex]);
        var sectionEnd = FindSectionEnd(lines, sectionIndex, sectionIndent);

        for (var i = sectionIndex + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == ListName)
                return InsertIntoIndentedList(uiData, lines, i, sectionEnd, missing);

            if (trimmed.StartsWith(ListName + " (") || trimmed.StartsWith(ListName + "("))
            {
                // The compact single-line form: checkBoxStates ( 42 true, 43 false )
                return trimmed.EndsWith(")")
                    ? InsertIntoInlineList(uiData, lines, i, missing)
                    : uiData; // unexpected shape, leave the blob alone
            }
        }

        // Section exists but holds no checkBoxStates list yet: append a compact one.
        return AppendNewInlineList(lines, sectionIndex, sectionEnd, sectionIndent, missing);
    }

    /// <summary>Length of the leading whitespace of a line.</summary>
    private static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;
        return i;
    }

    /// <summary>
    /// Index of the first line after the section header that no longer belongs to
    /// the section (shallower indentation or blank).
    /// </summary>
    private static int FindSectionEnd(List<string> lines, int sectionIndex, int sectionIndent)
    {
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0 || IndentOf(lines[i]) <= sectionIndent)
                return i;
        }

        return lines.Count;
    }

    /// <summary>Removes ids that already have an entry; true when the pair parses as one.</summary>
    private static bool TryConsumeEntry(string entry, SortedSet<uint> missing)
    {
        var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !uint.TryParse(parts[0], out var id))
            return false;

        missing.Remove(id);
        return true;
    }

    /// <summary>
    /// Adds missing entries to the multi-line list form:
    ///     checkBoxStates
    ///         42 true
    /// </summary>
    private static string InsertIntoIndentedList(string original, List<string> lines, int listIndex, int sectionEnd, SortedSet<uint> missing)
    {
        var listIndent = IndentOf(lines[listIndex]);
        var lastEntryIndex = listIndex;
        var entryLeading = (string)null;

        for (var i = listIndex + 1; i < sectionEnd; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || IndentOf(line) <= listIndent)
                break;

            lastEntryIndex = i;
            entryLeading ??= line[..IndentOf(line)];
            TryConsumeEntry(line.Trim(), missing);
        }

        if (missing.Count == 0)
            return original;

        entryLeading ??= lines[listIndex][..listIndent] + "    ";
        lines.InsertRange(lastEntryIndex + 1, missing.Select(id => $"{entryLeading}{id} true"));
        return string.Join(LineBreak, lines);
    }

    /// <summary>
    /// Adds missing entries to the single-line list form:
    ///     checkBoxStates ( 42 true, 43 false )
    /// </summary>
    private static string InsertIntoInlineList(string original, List<string> lines, int listIndex, SortedSet<uint> missing)
    {
        var line = lines[listIndex];
        var open = line.IndexOf('(');
        var close = line.LastIndexOf(')');
        if (open < 0 || close < open)
            return original;

        var entries = line[(open + 1)..close]
            .Split(',')
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        foreach (var entry in entries)
            TryConsumeEntry(entry, missing);

        if (missing.Count == 0)
            return original;

        entries.AddRange(missing.Select(id => $"{id} true"));
        lines[listIndex] = $"{line[..(open + 1)]} {string.Join(", ", entries)} {line[close..]}";
        return string.Join(LineBreak, lines);
    }

    /// <summary>
    /// Appends a compact checkBoxStates list as the last child of the section,
    /// using the indentation of its existing children when available.
    /// </summary>
    private static string AppendNewInlineList(List<string> lines, int sectionIndex, int sectionEnd, int sectionIndent, SortedSet<uint> missing)
    {
        var leading = new string(' ', sectionIndent + 4);
        if (sectionIndex + 1 < sectionEnd)
        {
            var firstChild = lines[sectionIndex + 1];
            leading = firstChild[..IndentOf(firstChild)];
        }

        var entries = string.Join(", ", missing.Select(id => $"{id} true"));
        lines.Insert(sectionEnd, $"{leading}{ListName} ( {entries} )");
        return string.Join(LineBreak, lines);
    }
}
