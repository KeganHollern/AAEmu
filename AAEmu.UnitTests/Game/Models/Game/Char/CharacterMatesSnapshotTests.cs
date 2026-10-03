using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterMatesSnapshotTests
{
    private static readonly DateTime SavedAt = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task CaptureActiveMateStates_CopiesCurrentRuntimeFieldsWithoutDismissal()
    {
        var (owner, mate, saved) = CreateFixture();
        owner.Mates.CaptureActiveMateStates(SavedAt);

        await Assert.That(saved.Hp).IsEqualTo(117);
        await Assert.That(saved.Mp).IsEqualTo(0);
        await Assert.That(saved.Level).IsEqualTo((ushort)42);
        await Assert.That(saved.Xp).IsEqualTo(4321);
        await Assert.That(saved.Mileage).IsEqualTo(987);
        await Assert.That(saved.Name).IsEqualTo("Current mate");
        await Assert.That(saved.UpdatedAt).IsEqualTo(SavedAt);
        await Assert.That(owner.ParentWorld.MateManager.GetActiveMateByTlId(owner.Id, mate.TlId))
            .IsSameReferenceAs(mate);
    }

    [Test]
    [Arguments("temporary")]
    [Arguments("zero-item")]
    [Arguments("owner-id")]
    [Arguments("owner-object")]
    [Arguments("world")]
    [Arguments("saved-owner")]
    [Arguments("saved-id")]
    [Arguments("saved-item")]
    [Arguments("saved-reference")]
    [Arguments("missing-row")]
    [Arguments("removing")]
    public async Task CaptureActiveMateStates_RejectsUnrelatedOrRetiredRuntimeObjects(string invalidState)
    {
        var (owner, mate, saved) = CreateFixture();
        switch (invalidState)
        {
            case "temporary": mate.IsTemporarySummon = true; break;
            case "zero-item": mate.ItemId = 0; break;
            case "owner-id": mate.OwnerId++; break;
            case "owner-object": mate.OwnerObjId++; break;
            case "world": SetWorld(mate, null); break;
            case "saved-owner": saved.Owner++; break;
            case "saved-id": saved.Id++; break;
            case "saved-item": saved.ItemId++; break;
            case "saved-reference": mate.DbInfo = new MateDb(); break;
            case "missing-row": SavedMates(owner).Clear(); break;
            case "removing":
                await Assert.That(owner.ParentWorld.MateManager.TryBeginMateRemoval(owner.Id, mate)).IsTrue();
                break;
        }

        owner.Mates.CaptureActiveMateStates(SavedAt);
        await Assert.That(saved.Hp).IsEqualTo(99);
        await Assert.That(saved.Mp).IsEqualTo(88);
        await Assert.That(saved.Name).IsEqualTo("Saved mate");
        await Assert.That(saved.UpdatedAt).IsEqualTo(SavedAt.AddDays(-1));
    }

    [Test]
    public async Task CaptureActiveMateStates_ReusedObjectIdsDoNotCaptureTheRemovedInstance()
    {
        var (owner, removed, saved) = CreateFixture();
        var manager = owner.ParentWorld.MateManager;
        await Assert.That(manager.TryBeginMateRemoval(owner.Id, removed)).IsTrue();
        manager.CompleteMateRemoval(removed);
        var replacement = new Mate
        {
            Id = removed.Id, ObjId = removed.ObjId, TlId = removed.TlId,
            OwnerId = owner.Id, OwnerObjId = owner.ObjId, ItemId = saved.ItemId,
            DbInfo = saved, Hp = 21, Mp = 22, Name = "Replacement", Level = 42
        };
        SetWorld(replacement, owner.ParentWorld);
        manager.TrackActiveMate(owner.Id, replacement);
        removed.Hp = 999;

        owner.Mates.CaptureActiveMateStates(SavedAt);
        await Assert.That(saved.Hp).IsEqualTo(21);
        await Assert.That(saved.Mp).IsEqualTo(22);
        await Assert.That(saved.Name).IsEqualTo("Replacement");
    }

    [Test]
    public async Task DespawnMate_StaleReferenceCannotOverwriteTheSavedReplacementOrRemoveIt()
    {
        var (owner, stale, saved) = CreateFixture();
        var manager = owner.ParentWorld.MateManager;
        await Assert.That(manager.TryBeginMateRemoval(owner.Id, stale)).IsTrue();
        manager.CompleteMateRemoval(stale);
        var replacement = new Mate
        {
            Id = stale.Id, ObjId = stale.ObjId, TlId = stale.TlId,
            OwnerId = owner.Id, OwnerObjId = owner.ObjId, ItemId = saved.ItemId,
            DbInfo = saved, Hp = 21, Mp = 22, Name = "Replacement", Level = 42
        };
        SetWorld(replacement, owner.ParentWorld);
        manager.TrackActiveMate(owner.Id, replacement);
        stale.Hp = 999;
        stale.Name = "Stale mount";

        owner.Mates.DespawnMate(stale);

        await Assert.That(saved.Hp).IsEqualTo(99);
        await Assert.That(saved.Name).IsEqualTo("Saved mate");
        await Assert.That(saved.UpdatedAt).IsEqualTo(SavedAt.AddDays(-1));
        await Assert.That(manager.GetActiveMateByTlId(owner.Id, replacement.TlId)).IsSameReferenceAs(replacement);
    }

    private static (Character Owner, Mate Mate, MateDb Saved) CreateFixture()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 0, Name = "mate snapshots" }, 0, true, 700);
        GC.SuppressFinalize(world);
        world.MateManager = new MateManager(world);
        var owner = new Character(null) { Id = 42, ObjId = 420 };
        owner.Mates = new CharacterMates(owner);
        SetWorld(owner, world);
        var saved = new MateDb
        {
            Id = 43, ItemId = 44, Owner = owner.Id, Hp = 99, Mp = 88,
            Name = "Saved mate", UpdatedAt = SavedAt.AddDays(-1), CreatedAt = SavedAt.AddDays(-2)
        };
        SavedMates(owner).Add(saved.ItemId, saved);
        var mate = new Mate
        {
            Id = saved.Id, ObjId = 430, TlId = 43, OwnerId = owner.Id, OwnerObjId = owner.ObjId,
            ItemId = saved.ItemId, DbInfo = saved, Hp = 117, Mp = 0, Level = 42,
            Experience = 4321, Mileage = 987, Name = "Current mate"
        };
        SetWorld(mate, world);
        world.MateManager.TrackActiveMate(owner.Id, mate);
        return (owner, mate, saved);
    }

    private static Dictionary<ulong, MateDb> SavedMates(Character owner) =>
        (Dictionary<ulong, MateDb>)typeof(CharacterMates)
            .GetField("_mates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner.Mates)!;

    private static void SetWorld(GameObject unit, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);
}
