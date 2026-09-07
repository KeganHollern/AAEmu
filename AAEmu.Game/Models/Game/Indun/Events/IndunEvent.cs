using AAEmu.Game.Models.Game.World;
using NLog;

namespace AAEmu.Game.Models.Game.Indun.Events;

public class IndunEvent
{
    protected readonly static Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly object _subscriptionLock = new();
    private readonly HashSet<WorldInstance> _subscribedWorlds = [];

    public uint Id { get; set; }
    public uint ConditionId { get; set; }
    public uint ZoneGroupId { get; set; }
    public uint StartActionId { get; set; }

    public void Subscribe(WorldInstance worldInstance)
    {
        lock (_subscriptionLock)
        {
            if (_subscribedWorlds.Contains(worldInstance))
                return;

            try
            {
                SubscribeCore(worldInstance);
                _subscribedWorlds.Add(worldInstance);
            }
            catch
            {
                UnSubscribeCore(worldInstance);
                throw;
            }
        }
    }

    public void UnSubscribe(WorldInstance worldInstance)
    {
        lock (_subscriptionLock)
        {
            if (!_subscribedWorlds.Remove(worldInstance))
                return;

            UnSubscribeCore(worldInstance);
        }
    }

    protected virtual void SubscribeCore(WorldInstance worldInstance) { }

    protected virtual void UnSubscribeCore(WorldInstance worldInstance) { }
}
