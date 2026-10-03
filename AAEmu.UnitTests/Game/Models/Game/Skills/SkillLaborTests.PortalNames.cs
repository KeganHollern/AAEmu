using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Utils;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    private string _portalNamePreviousLocale;

    [After(Test)]
    public void RestorePortalNameLocale()
    {
        if (_portalNamePreviousLocale != null)
            AppConfiguration.Instance.DefaultLanguage = _portalNamePreviousLocale;
    }

    [Test]
    [Arguments(11215u, 0, "ab")]
    [Arguments(16841u, 17, "The Admin")]
    [Arguments(11215u, 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments(16841u, 17, "Home\nName")]
    public async Task PortalNames_UsePortalBook_RejectsBeforeCostsCooldownsOrMutation(uint skillId, int portalId, string name)
    {
        ConfigurePortalNames();
        _material.TemplateId = 4045;
        _material.Template = new ItemTemplate { Id = 4045, UseSkillId = 11216 };
        _owner.Mp = 50;
        var skill = NewSkill();
        skill.Id = skill.Template.Id = skillId;
        skill.Template.ManaCost = 20;
        skill.Template.DefaultGcd = true;
        var initialGcd = _owner.GlobalCooldown;
        var initialSkillUse = _owner.SkillLastUsed;
        var data = new SkillObjectSavePortalInfo { Id = portalId, Name = name };

        var result = skill.Use(_owner, new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId),
            new SkillCastUnitTarget(_owner.ObjId), data, false, out var detail);

        await Assert.That(result).IsEqualTo(SkillResult.InvalidTarget);
        await Assert.That(detail).IsEqualTo(0u);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
        await Assert.That(_owner.GlobalCooldown).IsEqualTo(initialGcd);
        await Assert.That(_owner.SkillLastUsed).IsEqualTo(initialSkillUse);
        await Assert.That(_owner.Cooldowns.CheckCooldown(skill.Template)).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Mp).IsEqualTo(50);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.Portals.PrivatePortals.Count).IsEqualTo(1);
        await Assert.That(_owner.Portals.PrivatePortals[17].Name).IsEqualTo("My Home");
        await Assert.That(_owner.SkillTask).IsNull();
        await Assert.That(_owner.ActivePlotState).IsNull();
        await Assert.That(_owner.Packets).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(17)]
    public async Task PortalNames_UsePlotPayload_DoesNotDependOnPortalSkillIds(int portalId)
    {
        ConfigurePortalNames();
        var skill = NewSkill();
        skill.Template.Plot = new Plot();
        skill.Template.PlotOnly = true;
        _owner.InitializeLaborCache(0, DateTime.UtcNow);

        var result = skill.Use(_owner, new SkillCasterUnit(_owner.ObjId), new SkillCastUnitTarget(_owner.ObjId),
            new SkillObjectSavePortalInfo { Id = portalId, Name = "The ADMIN" }, false, out _);

        await Assert.That(result).IsEqualTo(SkillResult.InvalidTarget);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_owner.ActivePlotState).IsNull();
        await Assert.That(_owner.Portals.PrivatePortals[17].Name).IsEqualTo("My Home");
    }

    [Test]
    [Arguments(null)]
    [Arguments("ab")]
    [Arguments("The Admin")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments(".Home")]
    [Arguments("Home ")]
    [Arguments("Home\nName")]
    [Arguments("Home😀")]
    public async Task PortalNames_DirectSaveAndRename_RejectBeforeChangingPortalsOrAllocatingIds(string name)
    {
        ConfigurePortalNames();
        _owner.Portals.AddPrivatePortal(1, 2, 3, 0.5f, 4, name);
        var renamed = _owner.Portals.ChangePrivatePortalName(17, name);

        await Assert.That(renamed).IsFalse();
        await Assert.That(_owner.Portals.PrivatePortals.Count).IsEqualTo(1);
        await Assert.That(_owner.Portals.PrivatePortals[17].Name).IsEqualTo("My Home");
        await Assert.That(PrivateBookIdManager.Instance.GetNextId()).IsEqualTo(0x1000u);
    }

    [Test]
    public async Task PortalNames_SavePortalEffect_PreservesValidNameAndSavedLocation()
    {
        ConfigurePortalNames();
        _owner.Transform.Local.SetPosition(12, 23, 34);
        var position = _owner.Transform.World.Position;
        var data = new SkillObjectSavePortalInfo { Id = 0, Name = "Éva's Home 2" };

        new SavePortal().Execute(_owner, new SkillCasterUnit(_owner.ObjId), _owner,
            new SkillCastUnitTarget(_owner.ObjId), null, NewSkill(), data, DateTime.UtcNow, 0, 0, 0, 0);

        var portal = _owner.Portals.PrivatePortals[0x1000];
        await Assert.That(portal.Name).IsEqualTo(data.Name);
        await Assert.That(portal.Owner).IsEqualTo(_owner.Id);
        await Assert.That(portal.X).IsEqualTo(position.X);
        await Assert.That(portal.Y).IsEqualTo(position.Y);
        await Assert.That(portal.Z).IsEqualTo(position.Z);

        data.Id = (int)portal.Id;
        data.Name = "My Second HOME";
        new SavePortal().Execute(_owner, new SkillCasterUnit(_owner.ObjId), _owner,
            new SkillCastUnitTarget(_owner.ObjId), null, NewSkill(), data, DateTime.UtcNow, 0, 0, 0, 0);

        await Assert.That(portal.Name).IsEqualTo("My Second HOME");
        await Assert.That(_owner.Portals.PrivatePortals.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PortalNames_Rename_OnlyChangesTheOwnersPrivatePortal()
    {
        ConfigurePortalNames();
        var district = new Portal { Id = 18, Name = "A" };
        _owner.Portals.DistrictPortals.Add(district.Id, district);
        var anotherBook = new CharacterPortals(_owner);

        await Assert.That(anotherBook.ChangePrivatePortalName(17, "Other Home")).IsFalse();
        await Assert.That(_owner.Portals.ChangePrivatePortalName(18, "Other Home")).IsFalse();
        await Assert.That(_owner.Portals.ChangePrivatePortalName(19, "Other Home")).IsFalse();
        await Assert.That(_owner.Portals.PrivatePortals[17].Name).IsEqualTo("My Home");
        await Assert.That(district.Name).IsEqualTo("A");
    }

    private void ConfigurePortalNames()
    {
        _portalNamePreviousLocale = AppConfiguration.Instance.DefaultLanguage;
        AppConfiguration.Instance.DefaultLanguage = "en_us";
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE allowed_name_chars (id INTEGER, char TEXT, bytes INTEGER);
            CREATE TABLE blocked_texts
                (id INTEGER, utf8str TEXT, bytes INTEGER, check_name TEXT, check_chat TEXT, partial_match TEXT);
            INSERT INTO blocked_texts VALUES (1, 'Admin', 5, 't', 'f', 't');
            """;
        command.ExecuteNonQuery();
        var names = new NameGameData();
        names.Load(connection);
        SetInstance(names);

        var ids = new PrivateBookIdManager();
        var idField = typeof(PrivateBookIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.Add(idField, idField.GetValue(null));
        idField.SetValue(null, ids);
        typeof(IdManager).GetField("_freeIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ids, new BitSet(8));
        typeof(IdManager).GetField("_freeIdCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ids, 8);

        _owner.Portals = new CharacterPortals(_owner);
        _owner.Portals.PrivatePortals.Add(17, new Portal { Id = 17, Owner = _owner.Id, Name = "My Home" });
    }
}
