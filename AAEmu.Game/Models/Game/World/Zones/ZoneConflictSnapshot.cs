namespace AAEmu.Game.Models.Game.World.Zones;

public readonly record struct ZoneConflictSnapshot(ZoneConflictType State, uint KillCount, DateTime NextStateTime);
