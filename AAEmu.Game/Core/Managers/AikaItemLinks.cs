using System.Text;
using System.Text.RegularExpressions;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Core.Managers;

internal static partial class AikaItemLinks
{
    // Stock loot UI examples encode 235 characters. The r208022 item writer encodes 192 bytes into 256.
    [GeneratedRegex(@"\|i([0-9]{1,10}),[0-9]{1,3},[0-9A-Za-z*+]{235}(?:[0-9A-Za-z*+]{21})?;", RegexOptions.CultureInvariant)]
    private static partial Regex ItemLinkPattern();

    internal static string ForHistory(string message, int limit)
    {
        var matches = ItemLinkPattern().Matches(message);
        if (matches.Count == 0)
            return message;

        var links = new List<Link>(matches.Count);
        var available = limit - message.Length;
        foreach (Match match in matches)
        {
            if (!uint.TryParse(match.Groups[1].ValueSpan, out var id))
                continue;
            links.Add(new Link(match, id, ItemManager.Instance.GetTemplate(id)));
            available += match.Length;
        }

        // Reserve the player's own words first. Keep each label at its original place in the question.
        foreach (var link in links)
            link.TrySet($"[#{link.Id}]", ref available);

        var texts = LocalizationManager.Instance;
        foreach (var link in links)
        {
            var name = link.Item == null ? $"Unknown item #{link.Id}" : texts.Get("items", "name", link.Id, link.Item.Name);
            if (!string.IsNullOrWhiteSpace(name))
                link.TrySet($"[{name}]", ref available);
        }

        foreach (var link in links)
        {
            var item = link.Item;
            if (item == null || link.Label.Length == 0)
                continue;

            // The public tooltip describes the effect. Some skill names are internal development notes.
            var use = item.UseSkillId == 0 ? "" : texts.Get("skills", "desc", item.UseSkillId);
            if (!link.TryAddTooltip(use, ref available))
                link.TryAddTooltip(texts.Get("items", "description", link.Id), ref available);
        }

        foreach (var link in links)
        {
            var item = link.Item;
            if (item == null || link.Label.Length == 0)
                continue;
            var category = texts.Get("item_categories", "name", item.CategoryId);
            if (!string.IsNullOrWhiteSpace(category))
                link.TrySet($"{link.Label[..^1]}, {category}]", ref available);
            if (item.LevelRequirement > 0)
                link.TrySet($"{link.Label[..^1]}, requires level {item.LevelRequirement}]", ref available);
        }

        var result = new StringBuilder();
        var start = 0;
        foreach (var link in links)
        {
            result.Append(message, start, link.Match.Index - start).Append(link.Display);
            start = link.Match.Index + link.Match.Length;
        }
        return result.Append(message, start, message.Length - start).ToString();
    }

    private sealed class Link(Match match, uint id, ItemTemplate item)
    {
        internal Match Match { get; } = match;
        internal uint Id { get; } = id;
        internal ItemTemplate Item { get; } = item;
        internal string Label { get; private set; } = "";
        private string Tooltip { get; set; }
        internal string Display => Tooltip == null ? Label : $"{Label[..^1]}. {Tooltip}]";

        internal void TrySet(string label, ref int available)
        {
            var cost = label.Length - Label.Length;
            if (cost > available)
                return;
            available -= cost;
            Label = label;
        }

        internal bool TryAddTooltip(string text, ref int available)
        {
            // Game does not resolve client color escapes or calculated tooltip variables in chat.
            if (string.IsNullOrWhiteSpace(text) || text.Length + 2 > available || text.IndexOfAny(['|', '{', '}', '\r', '\n']) >= 0)
                return false;
            Tooltip = text;
            available -= text.Length + 2;
            return true;
        }
    }
}
