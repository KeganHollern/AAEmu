namespace AAEmu.Game.Models.Game.Dominions;

public sealed record DominionState(ushort ZoneGroupId, uint SiegeZoneId, uint OwnerExpeditionId,
    int TaxRate, long HouseTaxBalance)
{
    public static DominionState Unclaimed(ushort zoneGroupId, uint siegeZoneId) => new(zoneGroupId, siegeZoneId, 0, 0, 0);

    public DominionData ToPacketData()
    {
        // Ownership needs a confirmed claim target and territory data. Do not synthesize it.
        if (OwnerExpeditionId != 0 || TaxRate != 0 || HouseTaxBalance < 0 || HouseTaxBalance > int.MaxValue)
            throw new InvalidOperationException("Only unclaimed dominion state is supported before the claim lifecycle is defined.");
        return new DominionData
        {
            ZoneId = ZoneGroupId,
            CurHouseTaxMoney = (int)HouseTaxBalance,
            TerritoryData = new DominionTerritoryData(),
            SiegeTimers = new DominionSiegeTimers
            {
                UnkData = new DominionUnkData { UnkIds = [] },
                Unk2Data = new DominionUnkData { UnkIds = [] }
            }
        };
    }
}
