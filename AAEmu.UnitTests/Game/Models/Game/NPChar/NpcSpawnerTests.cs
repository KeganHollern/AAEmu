using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Covers the runtime activation gate introduced for aaemu-cluster#92 (#94/#97): IsActive follows
/// the authored activation_state until a script overrides it via Activate()/Deactivate().
/// </summary>
public class NpcSpawnerTests
{
    [Test]
    public async Task IsActive_WithoutTemplate_DefaultsToTrue()
    {
        var spawner = new NpcSpawner();

        await Assert.That(spawner.IsActive).IsTrue();
    }

    [Test]
    public async Task IsActive_FollowsAuthoredActivationState()
    {
        var active = new NpcSpawner { Template = new NpcSpawnerTemplate { ActivationState = true } };
        var inactive = new NpcSpawner { Template = new NpcSpawnerTemplate { ActivationState = false } };

        await Assert.That(active.IsActive).IsTrue();
        await Assert.That(inactive.IsActive).IsFalse();
    }

    [Test]
    public async Task ActivateAndDeactivate_OverrideAuthoredState()
    {
        var spawner = new NpcSpawner { Template = new NpcSpawnerTemplate { ActivationState = false } };

        spawner.Activate();
        await Assert.That(spawner.IsActive).IsTrue();

        spawner.Deactivate();
        await Assert.That(spawner.IsActive).IsFalse();
    }

    [Test]
    public async Task Clone_BeforeTemplateAssignment_DerivesIsActiveFromLaterTemplate()
    {
        // SpawnManager.AddNpcSpawner clones the dump-point spawner first and assigns the compact
        // template afterwards; the runtime gate must pick up that later assignment.
        var source = new NpcSpawner();
        var clone = NpcSpawner.Clone(source);

        clone.Template = new NpcSpawnerTemplate { ActivationState = false };

        await Assert.That(clone.IsActive).IsFalse();
        await Assert.That(source.IsActive).IsTrue();
    }
}
