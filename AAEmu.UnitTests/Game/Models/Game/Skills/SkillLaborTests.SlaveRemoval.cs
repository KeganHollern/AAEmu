using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments("combat")]
    [Arguments("visibility")]
    [Arguments("cargo")]
    public async Task VehicleReplacement_RejectionCancelsChildAndParentBatchWithoutCost(string rejection)
    {
        var worldManager = new WorldManager(null, null, null, null, null);
        SetInstance(worldManager);
        SetInstance(new ZoneManager(null, null));
        var data = new SlaveGameData();
        SetField(data, "_slaveTemplates", new Dictionary<uint, SlaveTemplate> { [1] = new() { Id = 1 } });
        SetInstance(data);
        var world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0)
        { Regions = new Region[16, 16] };
        world.Regions[0, 0] = new Region(world, 0, 0, 0);
        world.SlaveManager = new SlaveManager(world);
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = world });
        _owner.ParentWorld = world;
        _owner.Region = world.Regions[0, 0];
        var slave = new Slave
        {
            ObjId = 20, TlId = 30, Summoner = _owner, Hp = 100, ParentWorld = world,
            Template = data.GetSlaveTemplate(1), IsVisible = rejection != "visibility"
        };
        world.AddObject(slave);
        world.Regions[0, 0].AddObject(slave);
        if (rejection == "combat")
            slave.IsInBattle = true;
        if (rejection == "cargo")
            slave.AttachedDoodads.Add(new Doodad { ItemId = 42 });
        _material.Template = new SummonSlaveTemplate { Id = _material.TemplateId, SlaveId = 1 };
        var parentSkill = NewSkill();
        var childSkill = new Skill(new SkillTemplate { Id = 777 });
        var committed = false;
        parentSkill.CommitLaborBatch = (_, _) => { committed = true; return true; };
        var completionEffect = false;

        var accepted = SkillLaborBatch.Run(_owner, parentSkill, true, () =>
        {
            SkillLaborBatch.Current.Consume(_owner.Inventory.Bag, _material.TemplateId, 1, _material);
            SkillLaborBatch.Current.AfterCommit(() => completionEffect = true);
            new SpawnSlave().Execute(_owner, new SkillItem { ItemId = _material.Id }, _owner, null,
                null, childSkill, null, DateTime.UtcNow, 0, 0, 0, 0);
        });

        await Assert.That(accepted).IsFalse();
        await Assert.That(childSkill.Cancelled).IsTrue();
        await Assert.That(parentSkill.Cancelled).IsTrue();
        await Assert.That(committed || completionEffect).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(world.GetAllSlaves().Single()).IsSameReferenceAs(slave);
        await Assert.That(slave.AttachmentsRetired).IsFalse();
    }
}
