using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SkillLabor_ComposeSavesSongSheetPaperAndLaborTogether(bool fullBag, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var source = graph.AddItem(0);
        if (fullBag)
        {
            source.Count = 1;
            player.Inventory.Bag.ContainerSize = 1;
        }
        var sourceCount = source.Count;
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var ids = new Mock<IMusicIdManager>();
        var songId = player.Id + 80;
        ids.Setup(manager => manager.GetNextId()).Returns(songId);
        var items = new Mock<IItemManager>();
        var sheet = new MusicSheetItem(player.Id + 20UL,
            new MusicSheetTemplate { Id = Item.SheetMusic, MaxCount = 1, FixedGrade = -1 }, 1);
        items.Setup(manager => manager.Create(Item.SheetMusic, 1, 0, true)).Returns(() =>
        {
            Assert.True(graph.Items.AddItem(sheet));
            return sheet;
        });
        var music = new MusicManager(ids.Object, items.Object);
        music.UploadSong(player.Id, "Labor composition", "MML@cdef;", source.Id);
        var skill = new Skill(new SkillTemplate { Id = 22215, ConsumeLaborPower = 10 });
        if (failBeforeCommit)
            skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
            {
                write(context);
                throw new InvalidOperationException("Forced composition checkpoint failure");
            });
        Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () =>
        {
            Assert.True(music.CreateSheetMusic(player, source));
            Assert.Null(music.GetSongById(songId));
        }));
        Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM music WHERE id={songId} AND author={player.Id}"));
        Assert.Equal(failBeforeCommit ? sourceCount : sourceCount - 1,
            Scalar($"SELECT COALESCE(SUM(count),0) FROM items WHERE id={source.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={sheet.Id}"));
        Assert.Equal(failBeforeCommit ? sourceCount : sourceCount - 1, source.Count);
        ids.Verify(manager => manager.ReleaseId(songId), failBeforeCommit ? Times.Once() : Times.Never());
        if (failBeforeCommit)
        {
            Assert.Null(music.GetSongById(songId));
            Assert.Same(source, player.Inventory.Bag.Items.Single());
            Assert.Null(graph.Items.GetItemByItemId(sheet.Id));
        }
        else
        {
            Assert.Equal("MML@cdef;", music.GetSongById(songId).Song);
            var loaded = Assert.IsType<MusicSheetItem>(graph.ReloadLifecycle().Items.GetItemByItemId(sheet.Id));
            Assert.Equal(songId, loaded.SongId);
            Assert.Equal(player.Id, loaded.MadeUnitId);
            var reloadedMusic = new MusicManager(ids.Object, items.Object);
            reloadedMusic.Load();
            Assert.Equal("MML@cdef;", reloadedMusic.GetSongById(loaded.SongId).Song);
            var repeat = new Skill(new SkillTemplate { Id = 22215, ConsumeLaborPower = 10 });
            Assert.False(SkillLaborBatch.Run(player, repeat, true, () => music.CreateSheetMusic(player, source)));
            Assert.Equal(10, player.LaborPower);
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM music WHERE id={songId}"));
        }
    }

    [Fact]
    public void SkillLabor_ComposeRejectsUploadForAnotherSourceItem()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        var source = graph.AddItem(0);
        var ids = new Mock<IMusicIdManager>();
        var music = new MusicManager(ids.Object, Mock.Of<IItemManager>());
        music.UploadSong(player.Id, "Mismatched paper", "MML@c;", source.Id + 1);
        var skill = new Skill(new SkillTemplate { Id = 22215, ConsumeLaborPower = 10 });
        Assert.False(SkillLaborBatch.Run(player, skill, true, () => music.CreateSheetMusic(player, source)));
        Assert.Equal(20, player.LaborPower);
        Assert.Equal(5, source.Count);
        ids.Verify(manager => manager.GetNextId(), Times.Never());
    }
}
