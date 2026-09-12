using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;
using Microsoft.Data.Sqlite;

namespace AAEmu.Game.GameData;

[GameData]
public class PriestBuffGameData : Singleton<PriestBuffGameData>, IGameDataLoader
{
    private readonly Dictionary<uint, PriestBuffOffer> _offers = [];
    public IEnumerable<PriestBuffOffer> Offers => _offers.Values;
    public PriestBuffOffer Get(uint id) => _offers.GetValueOrDefault(id);

    public void Load(SqliteConnection connection)
    {
        _offers.Clear();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, buff_id, cost FROM priest_buffs";
        using var sqlite = command.ExecuteReader();
        using var reader = new SQLiteWrapperReader(sqlite);
        while (reader.Read())
        {
            var offer = new PriestBuffOffer(reader.GetUInt32("id"), reader.GetUInt32("buff_id"), reader.GetInt32("cost"));
            if (offer.Id == 0 || offer.BuffId == 0 || offer.CostPerLevel < 0)
                throw new InvalidDataException("Invalid priest buff offer");
            _offers.Add(offer.Id, offer);
        }
    }
    public void PostLoad() { }
}

public sealed record PriestBuffOffer(uint Id, uint BuffId, int CostPerLevel)
{
    public bool TryGetCost(byte level, out int cost)
    {
        var amount = (long)CostPerLevel * level;
        cost = 0;
        if (level == 0 || amount < 0 || amount > int.MaxValue)
            return false;
        cost = (int)amount;
        return true;
    }
}
