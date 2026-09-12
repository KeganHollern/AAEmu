using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;

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

    [Test]
    public async Task CancelOccurrence_RejectsLateDescendantsAndCancelsPlotsWithoutAffectingOtherSites()
    {
        var registry = new EventSpawnOwnershipRegistry();
        var root = new Npc { ObjId = 10 };
        var token = Token("a", 0, false);
        registry.Register(root, token);
        var state = new PlotState(root, null, root, null, null, null);
        var other = new Npc { ObjId = 20 };
        registry.Register(other, Token("b", 0, false));
        registry.CancelOccurrence("a");

        var accepted = registry.Register(new Npc { ObjId = 11 }, token with { CreatorObjId = 10 });
        await Assert.That(accepted).IsFalse();
        await Assert.That(state.CancellationRequested()).IsTrue();
        await Assert.That(other.TowerDefenseSpawnToken.Lifetime.IsCancelled).IsFalse();
        await Assert.That(registry.GetOccurrence("a").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Unregister_CancelsCapturedPlotEvenWhenItIsNotTheNpcsCurrentPlot()
    {
        var registry = new EventSpawnOwnershipRegistry();
        var npc = new Npc { ObjId = 30 };
        registry.Register(npc, Token("a", 0, false));
        var first = new PlotState(npc, null, npc, null, null, null);
        npc.ActivePlotState = new PlotState(npc, null, npc, null, null, null);
        registry.Unregister(npc.ObjId);

        await Assert.That(first.CancellationRequested()).IsTrue();
        await Assert.That(npc.ActivePlotState.CancellationRequested()).IsTrue();
    }

    [Test]
    public async Task Register_DuplicateObjectId_DoesNotOverwriteOwnershipToken()
    {
        var registry = new EventSpawnOwnershipRegistry();
        var npc = new Npc { ObjId = 40 };
        var original = Token("a", 0, false);
        registry.Register(npc, original);

        await Assert.That(registry.Register(npc, Token("b", 0, false))).IsFalse();
        await Assert.That(npc.TowerDefenseSpawnToken).IsSameReferenceAs(original);
    }

    private static TowerDefenseSpawnToken Token(string occurrence, uint creator, bool despawnWithCreator) =>
        new(occurrence, "event", "site", 2, 1, "action", creator, despawnWithCreator);
}
