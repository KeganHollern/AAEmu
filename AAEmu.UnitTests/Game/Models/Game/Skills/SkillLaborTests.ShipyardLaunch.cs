using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Shipyard;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    public async Task ShipyardLaunch_StartSkillGrantsOneScrollAndSchedulesTheCeremony()
    {
        var tasks = new TaskManager(null);
        SetInstance(tasks);
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new SkillGrantGameData());
        SetInstance(new ZoneManager(null, null));
        SetInstance(new WorldManager(null, null, null, null, null));
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        _owner.Level = 23;
        _owner.Skills = new CharacterSkills(_owner);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_owner, world);
        world.AddObject(_owner);

        var template = new ShipyardsTemplate { Id = 7, MainModelId = 966, ItemId = 15816, CeremonyAnimTime = 12000 };
        for (var step = 0; step < 6; step++)
            template.ShipyardSteps.Add(step, new ShipyardSteps { NumActions = 1 });
        var shipyard = new Shipyard
        {
            ObjId = 80, TemplateId = template.Id, Template = template, Hp = 10000,
            ShipyardData = new ShipyardData { Id = 1, TemplateId = template.Id, Type2 = _owner.Id }
        };
        shipyard.RestoreConstruction(-1, 0, false);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shipyard, world);
        world.AddObject(shipyard);

        var worlds = Mock.Of<IWorldManager>();
        worlds.GetCharacterById(_owner.Id).Returns(_owner);
        var commits = 0;
        var manager = new ShipyardManager(tasks, null, null, worlds.Object, null, SkillManager.Instance)
        {
            CommitPersistence = (_, _) => { commits++; return true; }
        };
        manager._shipyardsTemplate.Add(template.Id, template);
        SetInstance(manager);
        SetField(ItemManager.Instance, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [template.ItemId] = new() { Id = template.ItemId, MaxCount = 1 }
        });
        const uint skillId = 11002;
        var launch = new SkillTemplate
        {
            Id = skillId, TargetType = SkillTargetType.AnyUnit, TargetSelection = SkillTargetSelection.Target,
            DefaultGcd = true, MaxRange = 4, SourceAlive = true, TargetAlive = true,
            Effects = [new SkillEffect
            {
                ApplicationMethod = SkillEffectApplicationMethod.Target, StartLevel = 1, EndLevel = 99,
                Friendly = true, NonFriendly = true, Front = true, Back = true, Chance = 100,
                Template = new CraftEffect { WorldInteraction = WorldInteractionType.CraftGetItem }
            }]
        };
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = launch });
        SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _owner };
        _owner.Connection = connection;
        var body = new PacketStream().Write(skillId).Write((byte)0).WriteBc(_owner.ObjId)
            .Write((byte)0).WriteBc(shipyard.ObjId).Write((byte)0);
        await Assert.That(body.Count).IsEqualTo(13);

        var before = DateTime.UtcNow;
        new CSStartSkillPacket { Connection = connection }.Read(body);
        var after = DateTime.UtcNow;

        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(shipyard.ShipyardData.Step).IsEqualTo(1000);
        await Assert.That(shipyard.CeremonyEnd >= before.AddSeconds(12) && shipyard.CeremonyEnd <= after.AddSeconds(12)).IsTrue();
        var queued = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tasks)!;
        await Assert.That(queued.Values.OfType<ShipyardCompleteTask>().Single()._shipyard).IsSameReferenceAs(shipyard);
        await Assert.That(_owner.Packets.OfType<SCShipyardStatePacket>().Count()).IsEqualTo(1);

        body.Rollback();
        new CSStartSkillPacket { Connection = connection }.Read(body);

        var item = _owner.Inventory.Bag.Items.Single(item => item.TemplateId == template.ItemId);
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(shipyard.CompletionItemId).IsEqualTo(item.Id);
        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        session.SendPacket(Is<byte[]>(packet => packet[^1] == (byte)SkillResult.InvalidSkill)).WasCalled(Times.Once);
    }
}
