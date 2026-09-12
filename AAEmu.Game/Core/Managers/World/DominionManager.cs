using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Dominions;
using AAEmu.Game.Utils.DB;
using Microsoft.Data.Sqlite;
using NLog;

namespace AAEmu.Game.Core.Managers.World;

public class DominionManager : Singleton<DominionManager>, IDominionManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private Dictionary<ushort, DominionState> _states = [];

    public void Load()
    {
        using var compact = SQLite.CreateConnection();
        var authored = LoadAuthored(compact);
        using var connection = MySQL.CreateConnection();
        var saved = DominionStateStore.Load(connection);
        var states = new Dictionary<ushort, DominionState>();
        using var transaction = connection.BeginTransaction();
        foreach (var (zone, siege) in authored)
        {
            var state = saved.GetValueOrDefault(zone) ?? DominionState.Unclaimed(zone, siege);
            if (state.SiegeZoneId != siege)
                throw new InvalidOperationException($"Dominion {zone} does not match its authored siege zone {siege}.");
            _ = state.ToPacketData();
            if (!saved.ContainsKey(zone))
                DominionStateStore.Save(connection, transaction, state);
            states.Add(zone, state);
        }
        transaction.Commit();
        _states = states;
        Logger.Info("Loaded {0} unclaimed dominion territories.", states.Count);
    }

    internal static Dictionary<ushort, uint> LoadAuthored(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, zone_group_id FROM siege_zones ORDER BY zone_group_id";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<ushort, uint>();
        while (reader.Read())
            result.Add(checked((ushort)reader.GetInt32(1)), checked((uint)reader.GetInt32(0)));
        return result;
    }

    public DominionState[] GetStates() => _states.Values.OrderBy(state => state.ZoneGroupId).ToArray();

    public void SendStates(Character character)
    {
        foreach (var state in GetStates())
        {
            // Full data must precede rates: r208022 ignores a rate for an unknown dominion.
            character.SendPacket(new SCDominionDataPacket(state.ToPacketData(), false, false));
            character.SendPacket(new SCDominionTaxRatePacket(state.ZoneGroupId, state.TaxRate));
        }
    }
}
