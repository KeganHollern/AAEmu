using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.DoodadObj;

internal static class DoodadCreation
{
    internal static void InitializeAndSpawnTemporary(Doodad doodad, int delay = 0)
    {
        ArgumentNullException.ThrowIfNull(doodad);
        void Publish()
        {
            doodad.InitDoodad();
            if (delay > 0)
                Thread.Sleep(delay);
            doodad.Spawn();
        }

        if (SkillLaborBatch.Current is { } batch)
        {
            // Create allocated this unpublished object. A known failed commit can release it.
            batch.Enlist(null, () => ObjectIdManager.Instance.ReleaseId(doodad.ObjId));
            batch.AfterCommit(Publish);
        }
        else
            Publish();
    }
}
