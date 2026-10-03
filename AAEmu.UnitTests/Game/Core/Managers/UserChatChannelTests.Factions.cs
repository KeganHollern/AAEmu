using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class UserChatChannelTests
{
    [Test]
    public async Task ChangeFaction_ConcurrentDelivery_CompletesBeforeRemovingMembership()
    {
        var owner = Character(1);
        var guest = Character(2);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        Channels.Join(guest.Connection, "Travel", "", false);
        owner.Session.Packets.Clear();
        guest.Session.Packets.Clear();
        using var sending = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var changing = new ManualResetEventSlim();
        owner.Session.OnSend = () =>
        {
            sending.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the in-flight chat send.");
        };
        var send = Task.Run(() => Channels.Send(owner.Connection, key, "before change", 0, 0));
        await Assert.That(sending.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        var change = Task.Run(() =>
        {
            changing.Set();
            Channels.ChangeFaction(guest, new SystemFaction { Id = FactionsEnum.Pirate });
        });
        try
        {
            await Assert.That(changing.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(await Task.WhenAny(change, Task.Delay(50)) == change).IsFalse();
        }
        finally
        {
            release.Set();
        }
        await Task.WhenAll(send, change).WaitAsync(TimeSpan.FromSeconds(5));
        owner.Session.OnSend = null;
        await Assert.That(guest.Faction.Id).IsEqualTo(FactionsEnum.Pirate);
        await Assert.That(guest.Session.Packets.Count).IsEqualTo(2);
        await Assert.That(Body(guest.Session.Packets.Last()).ReadUInt64()).IsEqualTo(key);
        guest.Session.Packets.Clear();

        await Assert.That(Channels.Send(owner.Connection, key, "after change", 0, 0)).IsTrue();
        await Assert.That(guest.Session.Packets).IsEmpty();
        await Assert.That(Channels.Send(guest.Connection, key, "hostile", 0, 0)).IsFalse();
    }

    [Test]
    public async Task SetFaction_CallbacksRunOutsideRegistryLockAndOldMembershipIsRemoved()
    {
        var owner = Character(1);
        var oldKey = Channels.Join(owner.Connection, "Travel", "", true);
        var factions = new FactionManager(null);
        typeof(FactionManager).GetField("_systemFactions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(factions, new Dictionary<FactionsEnum, SystemFaction>
            {
                [FactionsEnum.HaranyaAlliance] = new() { Id = FactionsEnum.HaranyaAlliance }
            });
        SetInstance(factions);
        SetInstance(new HousingManager(null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, new CharacterAchievements(owner, new AchievementGameData()));
        var callbackCompleted = false;
        ulong newKey = 0;
        owner.OnBroadcast = _ =>
        {
            // A different thread must enter the registry during the faction callback.
            var join = Task.Run(() => Channels.Join(owner.Connection, "New faction", "", true));
            callbackCompleted = join.Wait(TimeSpan.FromSeconds(2));
            if (callbackCompleted)
                newKey = join.Result;
        };

        owner.SetFaction(FactionsEnum.HaranyaAlliance);

        await Assert.That(callbackCompleted).IsTrue();
        await Assert.That(newKey).IsGreaterThan(0UL);
        await Assert.That(Channels.Send(owner.Connection, oldKey, "old", 0, 0)).IsFalse();
        await Assert.That(Channels.Send(owner.Connection, newKey, "new", 0, 0)).IsTrue();
    }
}
