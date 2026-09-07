using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Models.Game.Quests;

public partial class Quest
{
    private bool _baseRewardsQueued;
    private bool _rewardFailureReported;

    internal bool ValidateStepRewardReferences(QuestStep step)
    {
        if (!AllowItemRewards)
            return true;
        foreach (var component in step.Components.Values.Where(component => component.IsCurrentlyActive))
        foreach (var act in component.Acts)
        {
            var itemId = act.Template switch
            {
                QuestActSupplyItem item => item.ItemId,
                QuestActSupplySelectiveItem item when item.ThisSelectiveIndex == SelectedRewardIndex => item.ItemId,
                _ => (uint?)null
            };
            if (itemId.HasValue && !IsValidReward(new ItemCreationDefinition(itemId.Value, act.Template.Count)))
                return RewardDeliveryFailed(itemId.Value);
        }
        return true;
    }

    private static bool IsValidReward(ItemCreationDefinition reward)
    {
        var template = ItemManager.Instance.GetTemplate(reward.TemplateId);
        return template != null && template.MaxCount > 0 && reward.Count >= 0;
    }

    private bool RewardDeliveryFailed(uint itemId = 0)
    {
        if (!_rewardFailureReported)
        {
            Logger.Error("Quest reward delivery pending: quest={QuestId}, owner={OwnerId}, step={Step}, item={ItemId}. No completion is recorded. Correct invalid reward content or resolve the delivery error, then retry; see Docs/customized/quest-reward-delivery.md.",
                TemplateId, Owner.Id, Step, itemId);
            Owner.SendErrorMessage(ErrorMessageType.MailUnknownFailure);
            _rewardFailureReported = true;
        }
        return false;
    }

    private bool DistributeItemRewards()
    {
        // Aggro ranking can explicitly exclude item rewards while allowing quest completion.
        if (!AllowItemRewards)
        {
            QuestRewardItemsPool.Clear();
            return true;
        }
        foreach (var reward in QuestRewardItemsPool)
            if (!IsValidReward(reward))
                return RewardDeliveryFailed(reward.TemplateId);

        QuestRewardItemsPool.RemoveAll(reward => reward.Count == 0);
        if (QuestRewardItemsPool.Count == 0)
            return true;

        if (Owner.Inventory.Bag.FreeSlotCount < QuestRewardItemsPool.Count)
            return MailItemRewards();

        foreach (var reward in QuestRewardItemsPool.ToArray())
        {
            bool delivered;
            if (ItemManager.Instance.IsAutoEquipTradePack(reward.TemplateId))
            {
                delivered = Owner.Inventory.TryEquipNewBackPack(ItemTaskType.QuestSupplyItems,
                    reward.TemplateId, reward.Count, reward.GradeId);
                if (delivered)
                    reward.Count = 0;
            }
            else
            {
                delivered = Owner.Inventory.Bag.AcquireDefaultItemEx(ItemTaskType.QuestSupplyItems,
                    reward.TemplateId, reward.Count, reward.GradeId, out _, out _, 0,
                    onGranted: count => reward.Count -= count);
            }
            if (reward.Count == 0)
                QuestRewardItemsPool.Remove(reward);
            if (!delivered)
                return RewardDeliveryFailed(reward.TemplateId);
        }
        return true;
    }

    private bool MailItemRewards()
    {
        // Keep currency in its pool until all mandatory items are delivered.
        if (!MailManager.Instance.TryCreateQuestRewardMails(Owner, this, QuestRewardItemsPool, out var mails))
            return RewardDeliveryFailed();

        for (var index = 0; index < mails.Count; index++)
        {
            var prepared = mails[index];
            var mail = prepared.Mail;
            if (!mail.Send())
            {
                MailManager.Instance.DiscardUnsentQuestRewardMails(mails.Skip(index).Select(preparedMail => preparedMail.Mail));
                return RewardDeliveryFailed();
            }
            foreach (var (reward, count) in prepared.Rewards)
            {
                reward.Count -= count;
                if (reward.Count == 0)
                    QuestRewardItemsPool.Remove(reward);
            }
        }
        Owner.SendPacket(new SCQuestRewardedByMailPacket([TemplateId]));
        return QuestRewardItemsPool.Count == 0;
    }

    private void WriteRewardState(PacketStream stream)
    {
        stream.Write((byte)1);
        stream.Write(SelectedRewardIndex);
        stream.Write(_baseRewardsQueued);
        stream.Write(AllowItemRewards);
        stream.Write(QuestRewardRatio);
        stream.Write(QuestRewardCoinsPool);
        stream.Write(QuestRewardExpPool);
        WriteRewardItems(stream, QuestRewardItemsPool);
        WriteRewardItems(stream, QuestCleanupItemsPool);
        stream.Write(checked((ushort)AppliedSideEffectActIds.Count));
        foreach (var id in AppliedSideEffectActIds.Order())
            stream.Write(id);
        stream.Write(checked((ushort)AppliedComponentEffectIds.Count));
        foreach (var id in AppliedComponentEffectIds.Order())
            stream.Write(id);
    }

    private void ReadRewardState(PacketStream stream)
    {
        if (stream.LeftBytes == 0 || stream.ReadByte() != 1)
            return;
        SelectedRewardIndex = stream.ReadInt32();
        _baseRewardsQueued = stream.ReadBoolean();
        AllowItemRewards = stream.ReadBoolean();
        QuestRewardRatio = stream.ReadDouble();
        QuestRewardCoinsPool = stream.ReadInt32();
        QuestRewardExpPool = stream.ReadInt32();
        ReadRewardItems(stream, QuestRewardItemsPool);
        ReadRewardItems(stream, QuestCleanupItemsPool);
        var actCount = stream.ReadUInt16();
        for (var index = 0; index < actCount; index++)
            AppliedSideEffectActIds.Add(stream.ReadUInt32());
        var componentCount = stream.ReadUInt16();
        for (var index = 0; index < componentCount; index++)
            AppliedComponentEffectIds.Add(stream.ReadUInt32());
    }

    private static void WriteRewardItems(PacketStream stream, List<ItemCreationDefinition> rewards)
    {
        stream.Write(checked((ushort)rewards.Count));
        foreach (var reward in rewards)
        {
            stream.Write(reward.TemplateId);
            stream.Write(reward.Count);
            stream.Write(reward.GradeId);
        }
    }

    private static void ReadRewardItems(PacketStream stream, List<ItemCreationDefinition> rewards)
    {
        rewards.Clear();
        var count = stream.ReadUInt16();
        for (var index = 0; index < count; index++)
            rewards.Add(new ItemCreationDefinition(stream.ReadUInt32(), stream.ReadInt32(), stream.ReadInt32()));
    }
}
