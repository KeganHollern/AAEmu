using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class EventSpawnOwnershipRegistryTests
{
    [Test]
    public async Task Registry_ScopesOwnedNpcsByOccurrenceAndCreator()
    {
        var registry = new EventSpawnOwnershipRegistry();
        var parent = new Npc { ObjId = 10 };
        var child = new Npc { ObjId = 11 };
        registry.Register(parent, Token("occurrence-a", 0, false));
        registry.Register(child, Token("occurrence-a", 10, true));
        registry.Register(new Npc { ObjId = 12 }, Token("occurrence-b", 0, false));

        await Assert.That(registry.GetOccurrence("occurrence-a").Count).IsEqualTo(2);
        await Assert.That(registry.GetChildren(10).Single().Npc.ObjId).IsEqualTo(11u);
        await Assert.That(registry.TryGet(12, out _)).IsTrue();
    }

    [Test]
    public async Task Registry_UnregisterClearsNpcToken()
    {
        var registry = new EventSpawnOwnershipRegistry();
        var npc = new Npc { ObjId = 21 };
        registry.Register(npc, Token("occurrence-a", 0, false));

        registry.Unregister(npc.ObjId);

        await Assert.That(npc.TowerDefenseSpawnToken).IsNull();
        await Assert.That(registry.TryGet(npc.ObjId, out _)).IsFalse();
    }

    private static TowerDefenseSpawnToken Token(string occurrence, uint creator, bool despawnWithCreator) =>
        new(occurrence, "event", "site", 2, 1, "action", creator, despawnWithCreator);
}
