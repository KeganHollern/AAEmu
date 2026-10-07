using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class UiDataSaveManagerTests
{
    [Test]
    public async Task UiChanges_CoalesceAndFailedSaveRetainsDirtyState()
    {
        var player = new CharacterMock { Id = 1 };
        var world = Mock.Of<IWorldManager>();
        world.GetAllCharacters().Returns([player]);
        var calls = 0;
        using var manager = new UiDataSaveManager(world.Object)
        {
            WriteBatch = _ => { ++calls; throw new IOException("database unavailable"); }
        };
        for (var index = 0; index < 100; ++index)
        {
            player.SetOption(5, index.ToString());
            player.SaveOption(5);
        }
        await Assert.That(calls).IsEqualTo(0);
        manager.FlushPending();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(player.GetOption(5)).IsEqualTo("99");
        await Assert.That(player.HasPendingUiData).IsTrue();
        manager.WriteBatch = characters =>
        {
            ++calls;
            characters[0].AcknowledgeUiOptions([new KeyValuePair<ushort, string>(5, "99")]);
        };
        manager.FlushPending();
        manager.FlushPending();
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(player.HasPendingUiData).IsFalse();
    }

    [Test]
    public async Task UiChanges_ObsoleteAcknowledgementDoesNotClearNewValue()
    {
        var player = new CharacterMock { Id = 1 };
        player.SetOption(5, "old");
        player.SaveOption(5);
        player.SetOption(5, "new");
        player.SaveOption(5);
        player.AcknowledgeUiOptions([new KeyValuePair<ushort, string>(5, "old")]);
        await Assert.That(player.HasPendingUiData).IsTrue();
        player.AcknowledgeUiOptions([new KeyValuePair<ushort, string>(5, "new")]);
        await Assert.That(player.HasPendingUiData).IsFalse();
    }

    [Test]
    public async Task BatchLimit_RotatesAcrossCharactersAndDoesNotRetainDepartedCharacters()
    {
        var characters = Enumerable.Range(1, UiDataSaveManager.BatchSize + 1).Select(id =>
        {
            var player = new CharacterMock { Id = (uint)id };
            player.SetOption(5, "test");
            player.SaveOption(5);
            return (Character)player;
        }).ToList();
        var world = Mock.Of<IWorldManager>();
        world.GetAllCharacters().Returns(() => characters.ToList());
        var batches = new List<uint[]>();
        using var manager = new UiDataSaveManager(world.Object)
        {
            WriteBatch = batch => batches.Add(batch.Select(character => character.Id).ToArray())
        };
        manager.FlushPending();
        await Assert.That(batches[0].Length).IsEqualTo(UiDataSaveManager.BatchSize);
        manager.FlushPending();
        await Assert.That(batches[1][0]).IsEqualTo((uint)UiDataSaveManager.BatchSize + 1);
        characters.Clear();
        manager.FlushPending();
        await Assert.That(batches.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Stop_DoesNotRunMoreTimerWrites()
    {
        var world = Mock.Of<IWorldManager>();
        using var manager = new UiDataSaveManager(world.Object);
        manager.Dispose();
        manager.FlushPending();
        Mock.VerifyNoOtherCalls(world);
        await Task.CompletedTask;
    }
}
