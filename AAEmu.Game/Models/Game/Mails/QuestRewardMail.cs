using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Models.Game.Mails;

// Keep the authored reward identity even when item creation selects a fixed or generated grade.
public sealed record QuestRewardMail(BaseMail Mail, IReadOnlyList<(ItemCreationDefinition Reward, int Count)> Rewards);
