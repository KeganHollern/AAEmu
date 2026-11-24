using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class SlaveRemovalTests
{
    [Test]
    [Arguments("match")]
    [Arguments("other-account")]
    [Arguments("other-character")]
    [Arguments("other-world")]
    [Arguments("other-hull")]
    [Arguments("other-hull-owner")]
    [Arguments("other-scroll")]
    [Arguments("missing-scroll")]
    [Arguments("retired-hull")]
    [Arguments("previous-session-active")]
    public async Task SelectedOwner_OnlyResumesTheOriginalSavedVehicle(string condition)
    {
        _owner.AccountId = 9;
        if (condition != "previous-session-active")
            _owner.Connection.ActiveChar = null;
        var next = new RecordingCharacter
        {
            Id = _owner.Id, ObjId = _owner.ObjId, AccountId = _owner.AccountId, ParentWorld = _world
        };
        _slave.OwnerId = _owner.Id;
        var scroll = SetSavedSummonLocation();
        var items = new ItemManager(null, null, null, null, null, null);
        var containers = Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate)
            .ToDictionary(type => (ulong)type + 1, type => new ItemContainer(next.Id, type, false, next));
        SetField(items, "_allPersistentContainers", containers);
        Install(items);
        next.Inventory = new Inventory(next);
        var bag = next.Inventory.Bag;
        var carried = condition == "other-scroll"
            ? new SummonSlave { Id = scroll.Id, OwnerId = next.Id, SlotType = SlotType.Inventory, SlaveDbId = _slave.Id }
            : scroll;
        bag.Items.Add(carried);
        carried._holdingContainer = bag;
        switch (condition)
        {
            case "other-account": next.AccountId++; break;
            case "other-character": next.Id++; break;
            case "other-world": next.ParentWorld = _otherWorld; break;
            case "other-hull": scroll.SlaveDbId++; break;
            case "other-hull-owner": _slave.OwnerId++; break;
            case "missing-scroll": bag.Items.Clear(); break;
            case "retired-hull": _slave.AttachmentsRetired = true; break;
        }
        var unrelated = new Slave { Summoner = _owner };
        _slave.AttachedSlaves.Add(unrelated);
        using var pending = new CancellationTokenSource();
        _slave.CancelTokenSource = pending;

        _world.SlaveManager.ResumeSelectedOwnersVehicle(next);

        var accepted = condition == "match";
        await Assert.That(_slave.Summoner).IsSameReferenceAs(accepted ? next : _owner);
        await Assert.That(unrelated.Summoner).IsSameReferenceAs(_owner);
        await Assert.That(pending.IsCancellationRequested).IsEqualTo(accepted);
    }
}
