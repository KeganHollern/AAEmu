using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;
using NLog;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class NpcAggroLinkGameData : Singleton<NpcAggroLinkGameData>, IGameDataLoader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private Dictionary<uint, HashSet<uint>> _npcLinks = [];

    public void Load(SqliteConnection connection)
    {
        var linkIds = LoadIds(connection, "SELECT id FROM aggro_links");
        var npcIds = LoadIds(connection, "SELECT id FROM npcs");
        var npcLinks = new Dictionary<uint, HashSet<uint>>();
        var invalidRows = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT npc_id, aggro_link_id FROM npc_aggro_links";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var npcId = reader.GetUInt32("npc_id", 0);
            var linkId = reader.GetUInt32("aggro_link_id", 0);
            if (!npcIds.Contains(npcId) || !linkIds.Contains(linkId))
            {
                invalidRows++;
                continue;
            }

            if (!npcLinks.TryGetValue(npcId, out var links))
                npcLinks.Add(npcId, links = []);
            links.Add(linkId);
        }

        // Publish a complete snapshot. Reload must discard removed memberships,
        // and a failed read must not replace the current combat index.
        Volatile.Write(ref _npcLinks, npcLinks);
        if (invalidRows > 0)
            Logger.Warn("Ignored {0} NPC aggro-link rows with missing NPC or link references", invalidRows);
    }

    private static HashSet<uint> LoadIds(SqliteConnection connection, string query)
    {
        var ids = new HashSet<uint>();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var id = reader.GetUInt32("id", 0);
            if (id != 0)
                ids.Add(id);
        }
        return ids;
    }

    public bool AreLinked(uint firstNpcId, uint secondNpcId)
    {
        var snapshot = Volatile.Read(ref _npcLinks);
        if (!snapshot.TryGetValue(firstNpcId, out var first) ||
            !snapshot.TryGetValue(secondNpcId, out var second))
            return false;

        // Only inspect this pair's memberships, not the complete link table.
        return first.Count <= second.Count ? first.Overlaps(second) : second.Overlaps(first);
    }

    public void PostLoad() { }
}
