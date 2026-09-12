using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Network;

public sealed class QuestSphereLogoutTests
{
    [Test]
    public async Task LogoutAndReconnect_SameCharacterAndQuest_ReplacesTriggersWithoutChangingProgress()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "main_world" }, 0, true, 1);
        var manager = new SphereQuestManager(world);
        world.SphereQuestManager = manager;
        var volume = new SphereQuest { QuestId = 100, ComponentId = 101, Radius = 10 };
        SetField(manager, "_sphereQuests", new Dictionary<uint, List<SphereQuest>> { [101] = [volume] });
        var positions = GetField<Dictionary<uint, (Character Character, Vector3 Position)>>(
            manager, "_questStartingLastPositionChecks");
        var departing = CreateCharacter(world, 7);
        var savedQuest = CreateQuest(departing);
        savedQuest.Objectives[0] = 1;
        savedQuest.Objectives[1] = 2;
        manager.AddSphereQuestTriggers(departing, savedQuest, 101, 0, 500);
        positions[departing.Id] = (departing, new Vector3(1, 2, 3));
        var otherPlayer = CreateCharacter(world, 8);
        var otherQuest = CreateQuest(otherPlayer);
        manager.AddSphereQuestTriggers(otherPlayer, otherQuest, 101, 0, 500);
        positions[otherPlayer.Id] = (otherPlayer, new Vector3(4, 5, 6));

        // This is the cleanup operation shared by graceful logout and hard disconnect.
        manager.RemoveSphereQuestTriggers(departing);

        await Assert.That(manager.GetSphereQuestTriggers().Count).IsEqualTo(1);
        await Assert.That(manager.GetSphereQuestTriggers()[0].Owner).IsSameReferenceAs(otherPlayer);
        await Assert.That(positions.ContainsKey(departing.Id)).IsFalse();
        await Assert.That(positions.ContainsKey(otherPlayer.Id)).IsTrue();
        await Assert.That(savedQuest.Objectives[0]).IsEqualTo(1);
        await Assert.That(savedQuest.Objectives[1]).IsEqualTo(2);
        await Assert.That(departing.Quests.ActiveQuests[100]).IsSameReferenceAs(savedQuest);

        var reconnected = CreateCharacter(world, departing.Id);
        var restoredQuest = CreateQuest(reconnected);
        restoredQuest.Objectives = savedQuest.Objectives.ToArray();
        manager.AddSphereQuestTriggers(reconnected, restoredQuest, 101, 0, 500);
        positions[reconnected.Id] = (reconnected, new Vector3(7, 8, 9));

        // A late second cleanup of the old session must keep the new session's state.
        manager.RemoveSphereQuestTriggers(departing);

        var reconnectedTrigger = manager.GetSphereQuestTriggers().Single(trigger => trigger.Owner.Id == departing.Id);
        await Assert.That(reconnectedTrigger.Owner).IsSameReferenceAs(reconnected);
        await Assert.That(reconnectedTrigger.Quest).IsSameReferenceAs(restoredQuest);
        await Assert.That(reconnectedTrigger.Sphere).IsSameReferenceAs(volume);
        await Assert.That(positions[reconnected.Id].Character).IsSameReferenceAs(reconnected);
        await Assert.That(positions[reconnected.Id].Position).IsEqualTo(new Vector3(7, 8, 9));
        await Assert.That(restoredQuest.Objectives[0]).IsEqualTo(1);
        await Assert.That(restoredQuest.Objectives[1]).IsEqualTo(2);
        await Assert.That(manager.GetSphereQuestTriggers().Count).IsEqualTo(2);
    }

    private static Character CreateCharacter(WorldInstance world, uint id)
    {
        var character = new CharacterMock { Id = id, ObjId = id, Transform = new Transform(null), ParentWorld = world };
        character.Quests = new CharacterQuests(character);
        return character;
    }

    private static Quest CreateQuest(Character owner)
    {
        var quest = new Quest(null, owner, null, null, null, null, null, false) { TemplateId = 100, Id = 123 };
        owner.Quests.ActiveQuests[quest.TemplateId] = quest;
        return quest;
    }

    private static T GetField<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
