using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(0, 200, true)]
    [InlineData(0, 201, false)]
    [InlineData(1, 400, true)]
    [InlineData(1, 401, false)]
    [InlineData(2, 600, true)]
    [InlineData(2, 601, false)]
    [InlineData(3, 800, true)]
    [InlineData(3, 801, false)]
    [InlineData(4, 1000, true)]
    [InlineData(4, 1001, false)]
    [InlineData(10, 1000, true)]
    [InlineData(10, 1001, false)]
    public void SkillLabor_ComposeUsesTheCurrentRankLimitBeforeAnyAssetCommit(int step, int noteCount, bool accepted)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender, (byte)step);
        var source = PrepareMusicAssets(graph);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        var notes = new string('c', noteCount);

        Assert.Equal(accepted, music.UploadSong(graph.Sender, "Rank boundary", notes, source.Id));
        Assert.Equal(accepted, SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));

        AssertMusicSettlement(graph, source, music, songId, sheetId, accepted);
        if (accepted)
            Assert.Equal(notes, music.GetSongById(songId).Song);
        else
            ids.Verify(manager => manager.GetNextId(), Times.Never());
    }

    [Theory]
    [InlineData("é", 200, true, true)]
    [InlineData("é", 201, false, false)]
    [InlineData("\U0001f3b5", 200, true, false)]
    [InlineData("\U0001f3b5", 201, false, false)]
    public void SkillLabor_ComposeCountsUnicodePointsAndRestoresAssetsOnUnsupportedStorage(
        string scalar, int count, bool uploadAccepted, bool persisted)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender);
        var source = PrepareMusicAssets(graph);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        var notes = string.Concat(Enumerable.Repeat(scalar, count));

        Assert.Equal(uploadAccepted, music.UploadSong(graph.Sender, "Unicode boundary", notes, source.Id));
        // The current music table uses MySQL utf8mb3. A supplementary code point
        // counts once, but its storage failure must roll back every staged asset.
        Assert.Equal(persisted, SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));

        AssertMusicSettlement(graph, source, music, songId, sheetId, persisted);
        if (persisted)
            Assert.Equal(notes, music.GetSongById(songId).Song);
        else if (!uploadAccepted)
            ids.Verify(manager => manager.GetNextId(), Times.Never());
        else
        {
            ids.Verify(manager => manager.GetNextId(), Times.Once());
            ids.Verify(manager => manager.ReleaseId(songId), Times.Once());
        }
    }

    [Fact]
    public void SkillLabor_ComposeRechecksTheRankAfterAnAcceptedUpload()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender, 1);
        var source = PrepareMusicAssets(graph);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        Assert.True(music.UploadSong(graph.Sender, "Before downgrade", new string('c', 201), source.Id));

        graph.Sender.Actability.Regrade((uint)ActabilityType.Artistry, false);
        Assert.Equal((byte)0, graph.Sender.Actability.Actabilities[(uint)ActabilityType.Artistry].Step);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.False(SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));

        AssertMusicSettlement(graph, source, music, songId, sheetId, false);
        Assert.Equal(0, Scalar($"SELECT step FROM actabilities WHERE owner={graph.Sender.Id} AND id={(uint)ActabilityType.Artistry}"));
        ids.Verify(manager => manager.GetNextId(), Times.Never());
    }

    [Fact]
    public void SkillLabor_RejectedNewUploadCannotComposeThePreviousQueuedSong()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender);
        var source = PrepareMusicAssets(graph);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        Assert.True(music.UploadSong(graph.Sender, "Old accepted song", "MML@c;", source.Id));

        Assert.False(music.UploadSong(graph.Sender, "New rejected song", new string('c', 201), source.Id));
        Assert.False(SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));

        AssertMusicSettlement(graph, source, music, songId, sheetId, false);
        ids.Verify(manager => manager.GetNextId(), Times.Never());
    }

    [Fact]
    public void SkillLabor_ComposeCheckpointFailureKeepsTheAcceptedUploadForOneRetry()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var limits = new MusicLimitServices(graph.Sender);
        var source = PrepareMusicAssets(graph);
        var music = CreateMusicTestManager(graph, limits.Data, out var ids, out var songId, out var sheetId);
        Assert.True(music.UploadSong(graph.Sender, "Retry exact boundary", new string('c', 200), source.Id));
        var failed = MusicTestSkill();
        failed.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
        {
            write(context);
            throw new InvalidOperationException("Forced music limit checkpoint failure");
        });

        Assert.False(SkillLaborBatch.Run(graph.Sender, failed, true,
            () => music.CreateSheetMusic(graph.Sender, source)));
        AssertMusicSettlement(graph, source, music, songId, sheetId, false);
        ids.Verify(manager => manager.ReleaseId(songId), Times.Once());

        graph.Sender.SkillCancelled = false;
        Assert.True(SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));
        AssertMusicSettlement(graph, source, music, songId, sheetId, true);
        Assert.Equal(new string('c', 200), music.GetSongById(songId).Song);
        Assert.False(SkillLaborBatch.Run(graph.Sender, MusicTestSkill(), true,
            () => music.CreateSheetMusic(graph.Sender, source)));
        AssertMusicSettlement(graph, source, music, songId, sheetId, true);
        ids.Verify(manager => manager.GetNextId(), Times.Exactly(2));
        ids.Verify(manager => manager.ReleaseId(songId), Times.Once());
    }

    private static Skill MusicTestSkill() => new(new SkillTemplate
    {
        Id = 22215, ConsumeLaborPower = 10, ActabilityGroupId = (int)ActabilityType.Artistry
    });

    private static Item PrepareMusicAssets(SendGraph graph, byte slot = 0)
    {
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var source = graph.AddItem(slot);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        return source;
    }

    private static MusicManager CreateMusicTestManager(SendGraph graph, MusicNoteGameData data,
        out Mock<IMusicIdManager> ids, out uint songId, out ulong sheetId)
    {
        var allocatedSongId = graph.Sender.Id + 80;
        var allocatedSheetId = graph.Sender.Id + 20UL;
        songId = allocatedSongId;
        sheetId = allocatedSheetId;
        ids = new Mock<IMusicIdManager>();
        ids.Setup(manager => manager.GetNextId()).Returns(allocatedSongId);
        var items = new Mock<IItemManager>();
        items.Setup(manager => manager.Create(Item.SheetMusic, 1, 0, true)).Returns(() =>
        {
            var sheet = new MusicSheetItem(allocatedSheetId,
                new MusicSheetTemplate { Id = Item.SheetMusic, MaxCount = 1, FixedGrade = -1 }, 1);
            Assert.True(graph.Items.AddItem(sheet));
            return sheet;
        });
        return new MusicManager(ids.Object, items.Object, data);
    }

    private static void AssertMusicSettlement(SendGraph graph, Item paper, MusicManager music,
        uint songId, ulong sheetId, bool committed)
    {
        var player = graph.Sender;
        Assert.Equal(committed ? 10 : 20, player.LaborPower);
        Assert.Equal(committed ? 10 : 20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(committed ? 4 : 5, paper.Count);
        Assert.Same(paper, player.Inventory.GetItemById(paper.Id));
        Assert.Equal(committed ? 4 : 5, Scalar($"SELECT count FROM items WHERE id={paper.Id}"));
        Assert.Equal(committed ? 17 : 7, player.Actability.Actabilities[(uint)ActabilityType.Artistry].Point);
        Assert.Equal(committed ? 17 : 7,
            Scalar($"SELECT point FROM actabilities WHERE owner={player.Id} AND id={(uint)ActabilityType.Artistry}"));
        Assert.Equal(committed ? 1 : 0, Scalar($"SELECT COUNT(*) FROM music WHERE id={songId} AND author={player.Id}"));
        Assert.Equal(committed ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={sheetId}"));
        if (committed)
        {
            var sheet = Assert.IsType<MusicSheetItem>(graph.Items.GetItemByItemId(sheetId));
            Assert.Equal(songId, sheet.SongId);
            Assert.Same(sheet, player.Inventory.GetItemById(sheetId));
            Assert.NotNull(music.GetSongById(songId));
        }
        else
        {
            Assert.Null(graph.Items.GetItemByItemId(sheetId));
            Assert.Null(player.Inventory.GetItemById(sheetId));
            Assert.Null(music.GetSongById(songId));
        }
    }

    private sealed class MusicLimitServices : IDisposable
    {
        private readonly CharacterManager _oldCharacters;
        private readonly WorldConfig _oldWorld;
        public MusicNoteGameData Data { get; } = new();

        public MusicLimitServices(Character player, byte step = 0)
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE music_note_limits (id INT, step INT, note_length INT);
                INSERT INTO music_note_limits VALUES
                    (1,0,200), (2,1,400), (3,2,600), (4,3,800), (5,4,1000),
                    (6,5,1000), (7,6,1000), (8,7,1000), (9,8,1000), (10,9,1000), (11,10,1000);
                """;
            command.ExecuteNonQuery();
            Data.Load(connection);
            var characters = new CharacterManager(null, null, null, null, null, null, null, null, null, null, null);
            SetField(characters, "_expertLimits", Enumerable.Range(0, 11).ToDictionary(rank => rank,
                _ => new ExpertLimit { UpLimit = 1000000, Show = true }));
            _oldCharacters = SwapSingleton(characters);
            _oldWorld = AppConfiguration.Instance.World;
            AppConfiguration.Instance.World = new WorldConfig { ActabilityRate = 1, ExpRate = 1, VocationRate = 1 };
            player.Actability = new CharacterActability(player);
            player.Actability.Actabilities[(uint)ActabilityType.Artistry] = new Actability(new ActabilityTemplate
                { Id = (uint)ActabilityType.Artistry }) { Step = step, Point = 7 };
        }

        public void Dispose()
        {
            SwapSingleton(_oldCharacters);
            AppConfiguration.Instance.World = _oldWorld;
        }
    }
}
