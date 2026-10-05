namespace AAEmu.Game.Models.Game.NPChar;

public partial class NpcSpawner
{
    /// <summary>
    /// Removes a live NPC without death rewards, retaining ordinary respawn ownership.
    /// </summary>
    internal void DespawnFromEffect(Npc npc)
    {
        lock (_spawnLock)
        {
            if (npc == null || npc.Despawned || npc.CombatRetired ||
                !ReferenceEquals(npc.Spawner, this) || npc.ParentWorld?.SpawnManager == null)
                return;

            // Death or schedule expiry may already own a queued removal.
            // Do not create a second replacement or replace its original due time.
            if (npc.Despawn == DateTime.MinValue)
                DoDespawn(npc);
            npc.Despawn = DateTime.UtcNow;
            npc.ParentWorld.SpawnManager.DespawnObject(npc);
        }
    }
}
