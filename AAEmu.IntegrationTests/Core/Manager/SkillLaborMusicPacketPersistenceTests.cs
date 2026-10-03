using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong_container")]
    [InlineData("wrong_slot")]
    [InlineData("wrong_owner")]
    [InlineData("wrong_holding_container")]
    [InlineData("missing_item")]
    [InlineData("truncated")]
    [InlineData("wrong_byte_count")]
    [InlineData("slot_is_not_rank")]
    public void SkillLabor_MusicPacketUsesTheActiveCharacterAndClearsRejectedUploads(string condition)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender);
        var player = graph.Sender;
        var source = PrepareMusicAssets(graph, condition == "slot_is_not_rank" ? (byte)4 : (byte)0);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        var previousMusic = SwapSingleton(music);
        try
        {
            var packet = new CSSaveUserMusicNotesPacket
            {
                Connection = new GameConnection(Mock.Of<ISession>()) { ActiveChar = player }
            };
            packet.Read(MusicUploadPacket(source.Id, SlotType.Inventory, (byte)source.Slot, "Old queued song", "MML@c;"));

            var itemId = condition == "missing_item" ? source.Id + 5000 : source.Id;
            var container = condition == "wrong_container" ? SlotType.Bank : SlotType.Inventory;
            var slot = (byte)(condition == "wrong_slot" ? source.Slot + 1 : source.Slot);
            var notes = new string('c', condition == "slot_is_not_rank" ? 201 : 200);
            var currentOwner = source.OwnerId;
            var holdingContainer = source._holdingContainer;
            try
            {
                if (condition == "wrong_owner")
                    source.OwnerId = graph.Receiver.Id;
                if (condition == "wrong_holding_container")
                    source._holdingContainer = player.Inventory.Warehouse;

                var request = MusicUploadPacket(itemId, container, slot, "New packet song", notes,
                    condition == "wrong_byte_count");
                if (condition == "truncated")
                    request = new PacketStream(request.GetBytes()[..^1]);
                packet.Read(request);
            }
            finally
            {
                source.OwnerId = currentOwner;
                source._holdingContainer = holdingContainer;
            }

            var accepted = condition == "valid";
            Assert.Equal(accepted, SkillLaborBatch.Run(player, MusicTestSkill(), true,
                () => music.CreateSheetMusic(player, source)));
            AssertMusicSettlement(graph, source, music, songId, sheetId, accepted);
            if (accepted)
            {
                Assert.Equal("New packet song", music.GetSongById(songId).Title);
                Assert.Equal(notes, music.GetSongById(songId).Song);
            }
            else
                ids.Verify(manager => manager.GetNextId(), Times.Never());
        }
        finally
        {
            SwapSingleton(previousMusic);
        }
    }

    private static PacketStream MusicUploadPacket(ulong itemId, SlotType container, byte slot,
        string title, string notes, bool wrongByteCount = false)
    {
        return new PacketStream()
            .Write(Encoding.UTF8.GetByteCount(notes) + (wrongByteCount ? 1 : 0))
            .Write(itemId).Write((byte)container).Write(slot)
            .Write(title).Write(notes, appendTerminator: true);
    }
}
