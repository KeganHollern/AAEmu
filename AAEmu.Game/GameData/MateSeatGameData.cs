using System.Xml.Linq;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.DoodadObj.Static;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

[GameData]
public sealed class MateSeatGameData : Singleton<MateSeatGameData>, IGameDataLoader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private Dictionary<uint, HashSet<AttachPointKind>> _seats = [];

    public void Load(SqliteConnection connection)
    {
        Load(connection, ClientFileManager.GetFileStream);
    }

    internal void Load(SqliteConnection connection, Func<string, Stream> openAsset)
    {
        var seats = new Dictionary<uint, HashSet<AttachPointKind>>();
        var assets = new Dictionary<string, HashSet<AttachPointKind>>(StringComparer.Ordinal);
        var names = new Dictionary<string, AttachPointKind>(StringComparer.Ordinal);
        using (var namesCommand = connection.CreateCommand())
        {
            // Native actor models use the actor alias, not the prefab alias ($driver).
            namesCommand.CommandText = "SELECT id, actor FROM model_attach_point_strings WHERE id IN (1, 2)";
            using var namesReader = namesCommand.ExecuteReader();
            while (namesReader.Read())
            {
                if (!namesReader.IsDBNull(1))
                    names.Add(namesReader.GetString(1), (AttachPointKind)namesReader.GetInt32(0));
            }
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT n.model_id, a.model_file
            FROM npcs n JOIN models m ON m.id = n.model_id
            JOIN actor_models a ON a.id = m.sub_id
            WHERE n.mate_kind_id > 0 AND m.sub_type = 'ActorModel'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var modelId = (uint)reader.GetInt64(0);
            if (reader.IsDBNull(1))
                continue;
            var path = reader.GetString(1).Replace('\\', '/').ToLowerInvariant();
            if (!path.EndsWith(".cdf", StringComparison.Ordinal))
                continue;
            if (!path.StartsWith("game/", StringComparison.Ordinal))
                path = "game/" + path;
            if (!assets.TryGetValue(path, out var modelSeats))
            {
                modelSeats = [];
                using var stream = openAsset(path);
                if (stream == null)
                {
                    Logger.Warn("Mate model {ModelId} has no seat definition at {Path}", modelId, path);
                }
                else
                {
                    var definition = XDocument.Load(stream);
                    foreach (var attachment in definition.Descendants("Attachment")
                        .Where(element => (string)element.Attribute("Type") == "CA_BONE"))
                    {
                        var name = (string)attachment.Attribute("AName");
                        if (name != null && names.TryGetValue(name, out var seat))
                            modelSeats.Add(seat);
                    }
                }
                assets.Add(path, modelSeats);
            }
            seats.Add(modelId, modelSeats);
        }
        _seats = seats;
    }

    public bool HasSeat(uint modelId, AttachPointKind seat)
    {
        return _seats.TryGetValue(modelId, out var seats) && seats.Contains(seat);
    }

    public void PostLoad() { }
}
