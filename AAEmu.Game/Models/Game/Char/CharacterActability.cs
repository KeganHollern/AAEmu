using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.StaticValues;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterActability(Character owner)
{
    public Dictionary<uint, Actability> Actabilities { get; set; } = [];

    public Character Owner { get; set; } = owner;

    /// <summary>
    /// Adds points to a specific ActAbility (life skill)
    /// </summary>
    /// <param name="id"></param>
    /// <param name="point"></param>
    /// <returns>The amount that was actually changed</returns>
    public int AddPoint(uint id, int point)
    {
        lock (Owner.StorePurchaseSyncRoot)
            return AddPointLocked(id, point);
    }

    private int AddPointLocked(uint id, int point)
    {
        if (!Actabilities.TryGetValue(id, out var actability))
            return 0;
        var previousPoints = actability.Point;
        actability.Point += point;

        var template = CharacterManager.Instance.GetExpertLimit(actability.Step);
        if (actability.Point > template.UpLimit)
            actability.Point = template.UpLimit;
        Owner.Achievements?.UpdateMaximum(CharRecordKind.GetActability, id, 0, (uint)Math.Max(actability.Point, 0));
        return actability.Point - previousPoints;
    }

    public void Regrade(uint id, bool isUpgrade)
    {
        lock (Owner.StorePurchaseSyncRoot)
            RegradeLocked(id, isUpgrade);
    }

    private void RegradeLocked(uint id, bool isUpgrade)
    {
        if (!Actabilities.TryGetValue(id, out var actability))
            return;

        if (isUpgrade)
        {
            var current = CharacterManager.Instance.GetExpertLimit(actability.Step);
            var next = CharacterManager.Instance.GetExpertLimit(actability.Step + 1);
            if (current == null || next is not { Show: true })
            {
                Owner.SendErrorMessage(ErrorMessageType.ActabilityCanUpgradeAnyMore);
                return;
            }
            if (actability.Point < current.UpLimit)
            {
                Owner.SendErrorMessage(ErrorMessageType.ActabilityNotEnoughPoint);
                return;
            }
            // Each cap includes all proficiencies at that rank or above it.
            var count = Actabilities.Values.Count(value => value.Step > actability.Step);
            if (next.ExpertLimitCount != 0 && count >= next.ExpertLimitCount + Owner.ExpandedExpert)
            {
                Owner.SendErrorMessage(ErrorMessageType.ActabilityCanUpgradeSelectionCountLimit);
                return;
            }
            actability.Step++;
        }
        else
        {
            var previous = CharacterManager.Instance.GetExpertLimit(actability.Step - 1);
            if (previous == null)
            {
                Owner.SendErrorMessage(ErrorMessageType.ActabilityCanDowngradeAnyMore);
                return;
            }
            actability.Step--;
            actability.Point = Math.Min(actability.Point, previous.UpLimit);
        }

        Owner.SendPacket(new SCExpertLimitModifiedPacket(isUpgrade, id, actability.Step));
    }

    public void ExpandExpert()
    {
        TryExpandExpert(() => SaveManager.Instance.TryCommitEconomy([Owner]));
    }

    internal bool TryExpandExpert(Func<bool> commit)
    {
        lock (Owner.StorePurchaseSyncRoot)
        {
            var expand = CharacterManager.Instance.GetExpandExpertLimit(Owner.ExpandedExpert);
            if (expand == null || expand.ExpandCount != Owner.ExpandedExpert + 1 ||
                expand.LifePoint < 0 || expand.ItemCount < 0)
                return false;
            if (Owner.VocationPoint < expand.LifePoint)
            {
                Owner.SendErrorMessage(ErrorMessageType.NotEnoughExpandItemAndMoney);
                return false;
            }

            using var inventory = new InventoryMutation(ItemTaskType.ExpandExpert);
            if (expand.ItemCount > 0)
            {
                var needed = expand.ItemCount;
                var bag = Owner.Inventory.Bag;
                foreach (var item in bag.Items.Where(item => item.TemplateId == expand.ItemId).OrderBy(item => item.Slot).ToArray())
                {
                    var count = Math.Min(needed, item.Count - TradeReservation.GetReservedCount(item));
                    if (count <= 0)
                        continue;
                    if (!inventory.TryConsume(bag, item, count))
                        break;
                    needed -= count;
                    if (needed == 0)
                        break;
                }
                if (needed != 0)
                {
                    Owner.SendErrorMessage(ErrorMessageType.NotEnoughExpandItem);
                    return false;
                }
            }

            var previousVocation = Owner.VocationPoint;
            var previousExpansion = Owner.ExpandedExpert;
            Owner.VocationPoint -= expand.LifePoint;
            Owner.ExpandedExpert = expand.ExpandCount;
            bool committed;
            try
            {
                committed = commit();
            }
            catch
            {
                // An unconfirmed commit stops Game. Keep the prepared state for that outcome.
                inventory.PreservePreparedState();
                throw;
            }
            if (!committed)
            {
                Owner.VocationPoint = previousVocation;
                Owner.ExpandedExpert = previousExpansion;
                Owner.SendErrorMessage(ErrorMessageType.InternalError);
                return false;
            }
            inventory.Complete();
            if (expand.LifePoint > 0)
                Owner.SendPacket(new SCGamePointChangedPacket((byte)GamePointKind.Vocation, -expand.LifePoint));
            Owner.SendPacket(new SCExpertExpandedPacket(Owner.ExpandedExpert));
            return true;
        }
    }

    public void Send()
    {
        Owner.SendPacket(new SCActabilityPacket(true, Actabilities.Values.ToArray()));
    }

    public void Load(MySqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM actabilities WHERE `owner` = @owner";
            command.Parameters.AddWithValue("@owner", Owner.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetUInt32("id");
                    var template = CharacterManager.Instance.GetActability(id);

                    var actability = new Actability(template)
                    {
                        Id = id,
                        Point = reader.GetInt32("point"),
                        Step = reader.GetByte("step")
                    };
                    Actabilities.Add(actability.Id, actability);
                }
            }
        }
    }

    public void Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        foreach (var actability in Actabilities.Values)
        {
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;

                command.CommandText = "REPLACE INTO actabilities(`id`,`point`,`step`,`owner`) VALUES (@id, @point, @step, @owner)";
                command.Parameters.AddWithValue("@id", (byte)actability.Id);
                command.Parameters.AddWithValue("@point", actability.Point);
                command.Parameters.AddWithValue("@step", actability.Step);
                command.Parameters.AddWithValue("@owner", Owner.Id);
                command.ExecuteNonQuery();
            }
        }
    }
}
