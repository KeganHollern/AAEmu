using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class ChatSpamGameData : Singleton<ChatSpamGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<uint, ChatSpamRule> _rules = [];
    private readonly Dictionary<uint, ChatSpamRuleDetail> _details = [];
    private readonly List<ChatSpamRule> _matchableRules = [];
    private readonly List<ChatSpamRuleValidationIssue> _validationIssues = [];

    public IReadOnlyDictionary<uint, ChatSpamRule> Rules => _rules;
    public IReadOnlyList<ChatSpamRuleValidationIssue> ValidationIssues => _validationIssues;

    public ChatSpamRule Get(uint id)
    {
        return _rules.GetValueOrDefault(id);
    }

    public bool TryMatch(string message, out ChatSpamMatch match)
    {
        match = null;
        if (string.IsNullOrEmpty(message))
            return false;

        foreach (var rule in _matchableRules)
        {
            if (!TryMatch(rule, message, out var matchedDetail))
                continue;

            match = new ChatSpamMatch(rule.Id, rule.Name, matchedDetail.Id, matchedDetail.Text);
            return true;
        }

        return false;
    }

    public void Load(SqliteConnection connection)
    {
        _rules.Clear();
        _details.Clear();
        _matchableRules.Clear();
        _validationIssues.Clear();

        LoadRules(connection);
        LoadDetails(connection);
    }

    public void PostLoad()
    {
        _matchableRules.Clear();

        foreach (var rule in _rules.Values.OrderBy(rule => rule.Id))
        {
            Validate(rule);
            if (rule.IsValid)
                _matchableRules.Add(rule);
        }

        Logger.Info(
            "Chat spam rules loaded: {0} rules, {1} valid, {2} details, {3} validation issue(s)",
            _rules.Count,
            _matchableRules.Count,
            _details.Count,
            _validationIssues.Count);

        foreach (var issue in _validationIssues)
            Logger.Error("Chat spam rule validation: {0}", issue.Message);
    }

    private void LoadRules(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM chat_spam_rules ORDER BY id";
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var rule = new ChatSpamRule
            {
                Id = reader.GetUInt32("id"),
                Name = reader.GetString("name", string.Empty)
            };

            if (_rules.TryAdd(rule.Id, rule))
                continue;

            _rules[rule.Id].IsValid = false;
            AddIssue(rule.Id, null, $"chat_spam_rules contains duplicate id {rule.Id}");
        }
    }

    private void LoadDetails(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, chat_spam_rule_id, text,
                                     detected_case_next_detail_id, not_detected_case_next_detail_id,
                                     start_node, end_node
                              FROM chat_spam_rule_details
                              ORDER BY id
                              """;
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var detail = new ChatSpamRuleDetail
            {
                Id = reader.GetUInt32("id"),
                ChatSpamRuleId = reader.GetUInt32("chat_spam_rule_id"),
                Text = reader.GetString("text", string.Empty),
                DetectedCaseNextDetailId = reader.GetUInt32("detected_case_next_detail_id"),
                NotDetectedCaseNextDetailId = reader.GetUInt32("not_detected_case_next_detail_id"),
                IsStartNode = reader.GetBoolean("start_node", true),
                IsEndNode = reader.GetBoolean("end_node", true)
            };

            if (_details.TryGetValue(detail.Id, out var duplicate))
            {
                InvalidateOwner(duplicate.ChatSpamRuleId);
                InvalidateOwner(detail.ChatSpamRuleId);
                AddIssue(detail.ChatSpamRuleId, detail.Id,
                    $"chat_spam_rule_details contains duplicate id {detail.Id}");
                continue;
            }

            _details.Add(detail.Id, detail);
            if (!_rules.TryGetValue(detail.ChatSpamRuleId, out var rule))
            {
                AddIssue(detail.ChatSpamRuleId, detail.Id,
                    $"chat_spam_rule_detail {detail.Id} references missing chat_spam_rule_id {detail.ChatSpamRuleId}; row skipped");
                continue;
            }

            rule.MutableDetails.Add(detail.Id, detail);
        }
    }

    private void Validate(ChatSpamRule rule)
    {
        var startNodes = rule.Details.Values.Where(detail => detail.IsStartNode).ToList();
        if (startNodes.Count != 1)
        {
            Invalidate(rule, null,
                $"chat_spam_rule {rule.Id} must contain exactly one start node; found {startNodes.Count}");
        }
        else
        {
            rule.StartNode = startNodes[0];
        }

        var endNodeCount = rule.Details.Values.Count(detail => detail.IsEndNode);
        if (endNodeCount != 1)
        {
            Invalidate(rule, null,
                $"chat_spam_rule {rule.Id} must contain exactly one end node; found {endNodeCount}");
        }

        foreach (var detail in rule.Details.Values)
        {
            if (!detail.IsEndNode && string.IsNullOrEmpty(detail.Text))
            {
                Invalidate(rule, detail.Id,
                    $"chat_spam_rule_detail {detail.Id} has empty nonterminal text");
            }

            ValidateEdge(rule, detail, detail.DetectedCaseNextDetailId, "detected_case_next_detail_id");
            ValidateEdge(rule, detail, detail.NotDetectedCaseNextDetailId, "not_detected_case_next_detail_id");
        }

        if (ContainsCycle(rule))
            Invalidate(rule, null, $"chat_spam_rule {rule.Id} contains a cycle");

        if (rule.StartNode != null && endNodeCount == 1 && !CanReachEndNode(rule))
            Invalidate(rule, null, $"chat_spam_rule {rule.Id} start node cannot reach its end node");
    }

    private void ValidateEdge(ChatSpamRule rule, ChatSpamRuleDetail source, uint targetId, string column)
    {
        if (targetId == 0)
            return;

        if (!_details.TryGetValue(targetId, out var target))
        {
            Invalidate(rule, source.Id,
                $"chat_spam_rule_detail {source.Id} has dangling {column} {targetId}");
            return;
        }

        if (target.ChatSpamRuleId != rule.Id)
        {
            Invalidate(rule, source.Id,
                $"chat_spam_rule_detail {source.Id} has {column} {targetId} in chat_spam_rule {target.ChatSpamRuleId}");
        }
    }

    private static bool ContainsCycle(ChatSpamRule rule)
    {
        var incomingEdges = new Dictionary<uint, int>(rule.Details.Count);
        foreach (var detailId in rule.Details.Keys)
            incomingEdges.Add(detailId, 0);

        foreach (var detail in rule.Details.Values)
        {
            AddIncomingEdge(detail.DetectedCaseNextDetailId);
            AddIncomingEdge(detail.NotDetectedCaseNextDetailId);
        }

        var pending = new Queue<uint>(incomingEdges.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (pending.Count > 0)
        {
            var detailId = pending.Dequeue();
            visited++;
            var detail = rule.Details[detailId];
            RemoveIncomingEdge(detail.DetectedCaseNextDetailId);
            RemoveIncomingEdge(detail.NotDetectedCaseNextDetailId);
        }

        return visited != rule.Details.Count;

        void AddIncomingEdge(uint targetId)
        {
            if (incomingEdges.ContainsKey(targetId))
                incomingEdges[targetId]++;
        }

        void RemoveIncomingEdge(uint targetId)
        {
            if (!incomingEdges.ContainsKey(targetId))
                return;

            incomingEdges[targetId]--;
            if (incomingEdges[targetId] == 0)
                pending.Enqueue(targetId);
        }
    }

    private static bool CanReachEndNode(ChatSpamRule rule)
    {
        var pending = new Stack<ChatSpamRuleDetail>();
        var visited = new HashSet<uint>();
        pending.Push(rule.StartNode);

        while (pending.Count > 0)
        {
            var detail = pending.Pop();
            if (!visited.Add(detail.Id))
                continue;
            if (detail.IsEndNode)
                return true;

            AddIfPresent(detail.DetectedCaseNextDetailId);
            AddIfPresent(detail.NotDetectedCaseNextDetailId);
        }

        return false;

        void AddIfPresent(uint detailId)
        {
            if (rule.Details.TryGetValue(detailId, out var detail))
                pending.Push(detail);
        }
    }

    private static bool TryMatch(ChatSpamRule rule, string message, out ChatSpamRuleDetail matchedDetail)
    {
        matchedDetail = null;
        var current = rule.StartNode;
        while (!current.IsEndNode)
        {
            var detected = message.Contains(current.Text, StringComparison.OrdinalIgnoreCase);
            if (detected)
                matchedDetail = current;

            var nextId = detected
                ? current.DetectedCaseNextDetailId
                : current.NotDetectedCaseNextDetailId;
            if (nextId == 0)
                return false;

            current = rule.Details[nextId];
        }

        matchedDetail ??= current;
        return true;
    }

    private void Invalidate(ChatSpamRule rule, uint? detailId, string message)
    {
        rule.IsValid = false;
        AddIssue(rule.Id, detailId, message);
    }

    private void InvalidateOwner(uint ruleId)
    {
        if (_rules.TryGetValue(ruleId, out var rule))
            rule.IsValid = false;
    }

    private void AddIssue(uint? ruleId, uint? detailId, string message)
    {
        _validationIssues.Add(new ChatSpamRuleValidationIssue(ruleId, detailId, message));
    }
}
