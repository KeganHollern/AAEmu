using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Stream;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CrestPurchase_CheckpointsExactOutputCostAndDataTogether(bool custom, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        player.Money = 100000;
        var material = graph.AddItem(0);
        material.Count = 1;
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
        templates[17663] = new UccTemplate { Id = 17663, MaxCount = 1, FixedGrade = 0 };
        var allocator = (IItemIdManager)typeof(ItemManager)
            .GetField("<itemIdManager>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
        Mock.Get(allocator).Setup(manager => manager.GetNextId()).Returns(player.Id + 90);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 1, true, 1);
        SetField(WorldManager.Instance, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new[] { new KeyValuePair<uint, WorldInstance>(1, world) }));
        var printer = new Doodad { ObjId = 99, TemplateId = 3038, ParentWorld = world };
        printer.CurrentFuncs.Add(new DoodadFunc { FuncId = 2, FuncType = nameof(DoodadFuncStampMaker) });
        world.AddObject(printer);
        player.ParentWorld = world;
        player.Connection = new GameConnection(Mock.Of<ISession>()) { ActiveChar = player };
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, true);
        player.CurrentInteractionObject = printer;
        var stream = new StreamConnection(Mock.Of<ISession>()) { GameConnection = player.Connection };
        var uccIds = new Mock<IUccIdManager>();
        var uccId = player.Id + 91;
        uccIds.Setup(ids => ids.GetNextId()).Returns(uccId);
        var maker = new DoodadFuncStampMaker { ConsumeMoney = 50000, ConsumeItemId = material.TemplateId, ConsumeCount = 1, ItemId = 17663 };
        var manager = new UccManager(uccIds.Object) { ResolveStampMaker = _ => maker };
        manager.Patterns.Add(3, 4);
        DefaultUcc ucc = custom ? new CustomUcc() : new DefaultUcc { Pattern2 = 3 };
        var data = new byte[144];
        foreach (var (offset, value) in new (int, uint)[] { (0, 0x20534444), (4, 124), (12, 1), (16, 1),
                     (24, 1), (28, 1), (76, 32), (80, 4), (84, 0x35545844) })
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Assert.True(manager.StartUpload(stream, 99, custom ? data.Length : 0, ucc));
        if (custom) Assert.True(manager.UploadPart(stream, new UccPart { Total = data.Length, Size = data.Length, Data = data }));
        if (failBeforeCommit)
        {
            manager.CommitPurchase = (character, crest) => graph.Save.TryCommitEconomy([character], context =>
            {
                using var command = context.Connection.CreateCommand();
                command.Transaction = context.Transaction;
                crest.Save(command);
                throw new InvalidOperationException("Test failure after UCC insert and before commit");
            });
        }
        Assert.Equal(!failBeforeCommit, manager.ConfirmDefaultUcc(stream, 0));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM uccs WHERE id={uccId}"));
        var expectedMoney = failBeforeCommit || custom ? 100000 : 50000;
        Assert.Equal(expectedMoney, player.Money);
        Assert.Equal(expectedMoney, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
        Assert.Equal(custom && !failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE owner={player.Id} AND ucc={uccId}"));
        if (!failBeforeCommit)
        {
            Assert.Equal(player.Id, Scalar($"SELECT uploader_id FROM uccs WHERE id={uccId}"));
            Assert.Equal(custom ? 144 : 0, Scalar($"SELECT COALESCE(OCTET_LENGTH(data),0) FROM uccs WHERE id={uccId}"));
            var reloaded = graph.ReloadLifecycle();
            var output = reloaded.Items.GetItemByItemId(player.Id + 90);
            Assert.NotNull(output);
            Assert.Equal((ulong)uccId, output.UccId);
            Assert.Equal(player.Id, output.OwnerId);
        }
        Assert.False(manager.ConfirmDefaultUcc(stream, 0));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM uccs WHERE id={uccId}"));
    }
}
