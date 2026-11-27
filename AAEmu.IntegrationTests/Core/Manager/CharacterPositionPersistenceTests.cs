using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.StaticValues;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(13215.904296875f, 14444.943359375f, 153.639495849609375f)]
    [InlineData(19976.763671875f, 12279.666015625f, 103.00810241699219f)]
    [InlineData(-1.2345678f, 0.12345678f, -25.765432f)]
    public void CharacterLoadAndSave_PreserveStoredPosition(float x, float y, float z)
    {
        using var fixture = new ShipyardGraph();
        var original = SaveCharacterPose(fixture, new Vector3(x, y, z), Vector3.Zero);
        var position = original.Transform.World.Position;
        using var db = MySQL.CreateConnection();
        var session = new GameConnection(null);
        Assert.True(session.TryAuthenticate(original.AccountId));
        for (var i = 0; i < 2; i++)
        {
            session.LoadAccount();
            var selected = session.Characters[original.Id];
            Assert.Equal(position, selected.Transform.World.Position);
            Assert.Equal(position, Character.Load(db, original.Id).Transform.World.Position);
            Assert.Equal(original.Money, selected.Money);
            Assert.Equal(original.Name, selected.Name);
            Assert.Equal(original.Hp, selected.Hp);
            Assert.Equal(original.Created, selected.Created);
            Assert.True(fixture.Graph.Save.TryCommitEconomy([selected]));
        }
        using var command = db.CreateCommand();
        command.CommandText = "SELECT CAST(x AS DOUBLE), CAST(y AS DOUBLE), CAST(z AS DOUBLE) FROM characters WHERE id=@id";
        command.Parameters.AddWithValue("@id", original.Id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((double)x, reader.GetDouble(0));
        Assert.Equal((double)y, reader.GetDouble(1));
        Assert.Equal((double)z, reader.GetDouble(2));
    }

    [Fact]
    public void CharacterLoadAndSave_PreserveRollPitchAndYaw()
    {
        using var fixture = new ShipyardGraph();
        var rotation = new Vector3(-0.375f, 0.25f, MathF.PI / 2);
        var original = SaveCharacterPose(fixture, new Vector3(100, 120, 40), rotation);
        using var db = MySQL.CreateConnection();
        for (var i = 0; i < 2; i++)
        {
            var selected = Character.Load(db, original.Id, original.AccountId);
            Assert.Equal(rotation, selected.Transform.World.Rotation);
            Assert.Equal(rotation, Character.Load(db, original.Id).Transform.World.Rotation);
            Assert.True(fixture.Graph.Save.TryCommitEconomy([selected]));
        }
        Assert.Equal(rotation, Character.Load(db, original.Id, original.AccountId).Transform.World.Rotation);
    }

    private static Character SaveCharacterPose(ShipyardGraph fixture, Vector3 position, Vector3 rotation)
    {
        ConfigureCharacterList(fixture);
        var original = fixture.Graph.Sender;
        original.Slots = Enumerable.Range(0, Character.MaxActionSlots).Select(_ => new ActionSlot()).ToArray();
        original.Level = 1;
        original.Hp = 90;
        original.Money = 10001;
        original.Created = new DateTime(2026, 11, 26, 8, 39, 42, DateTimeKind.Utc);
        original.Transform.Local.SetPosition(position, rotation);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({original.AccountId},100)");
        Assert.True(fixture.Graph.Save.TryCommitEconomy([original]));
        return original;
    }

    private static void ConfigureCharacterList(ShipyardGraph fixture)
    {
        var formulas = new FormulaManager();
        var constant = new UnitFormula();
        typeof(Formula).GetProperty("Expression", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(constant, (Func<Dictionary<string, double>, double>)(_ => 100));
        SetField(formulas, "_unitFormulas", new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>
        { [FormulaOwnerType.Character] = Enum.GetValues<UnitFormulaKind>().ToDictionary(kind => kind, _ => constant) });
        fixture.Replace(formulas);
        fixture.Replace(new AccountManager(null, null, TimeProvider.System));
        var experience = new ExperienceManager();
        var levels = new Mock<IExperienceLevelTemplateLoader>();
        levels.Setup(loader => loader.Load()).Returns([new ExperienceLevelTemplate { Level = 1 }]);
        experience.Load(levels.Object, 1, 1);
        fixture.Replace(experience);
        fixture.Replace(new ExpeditionManager(null, null, null, null));
        fixture.Replace(new CharacterManager(WorldManager.Instance, null, null, null, null, null, null, null, null, null, null));
        fixture.Replace(new HousingManager(null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        var itemData = new ItemGameData();
        SetField(itemData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
        fixture.Replace(itemData);
    }
}
