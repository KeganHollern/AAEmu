using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncRespawn : DoodadPhaseFuncTemplate
{
    public int MinTime { get; set; }
    public int MaxTime { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace("DoodadFuncRespawn: MinTime {0}, MaxTime {1}", MinTime, MaxTime);

        // Doodad spawn
        if (caster is Character character)
        {
            var placementPolicy = character.Transform.Parent is not null || character.Transform.StickyParent is not null
                ? DynamicDoodadPlacementPolicy.PreserveParentedHeight
                : DynamicDoodadPlacementPolicy.GroundToNearbySurface;
            var spawnPosition = DynamicDoodadPlacement.CreateForwardWorldPosition(character.Transform, 1f);
            if (!DynamicDoodadPlacement.TryResolve(caster.ParentWorld.Template.GeoData,
                    spawnPosition.AsPositionVector(),
                    placementPolicy, out var placementPosition))
            {
                Logger.Warn($"DoodadFuncRespawn: Cannot place doodad {owner.TemplateId} at {spawnPosition}");
                return false;
            }

            spawnPosition.Z = placementPosition.Z;
            var doodad = new DoodadSpawner
            {
                ParentWorld = character.ParentWorld,
                Id = owner.ObjId,
                UnitId = owner.TemplateId,
                Position = spawnPosition
            };
            doodad.Spawn(0);
        }

        return false;
    }
}
