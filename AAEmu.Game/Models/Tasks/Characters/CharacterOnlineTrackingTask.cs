using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.Game.Models.Tasks.Characters;

public class CharacterOnlineTrackingTask : Task
{
    public static TimeSpan CheckPrecision { get; set; } = TimeSpan.FromSeconds(5);
    private DateTime LastCheck { get; set; }
    private readonly object _lock = new();
    private bool Busy { get; set; }

    public CharacterOnlineTrackingTask()
    {
        LastCheck = DateTime.UtcNow;
        lock (_lock)
            Busy = false;
    }

    public override void Execute()
    {
        lock (_lock)
        {
            if (Busy)
                return;
            Busy = true;
        }
        var delta = DateTime.UtcNow - LastCheck;
        LastCheck = DateTime.UtcNow;

        // Loop all online players
        foreach (var character in WorldManager.Instance.GetAllCharacters())
        {
            // Update character time
            var lastSeconds = Math.Floor(character.OnlineTime.TotalSeconds);
            var lastHours = Math.Floor(character.OnlineTime.TotalHours);
            character.OnlineTime += delta;
            var newSeconds = Math.Floor(character.OnlineTime.TotalSeconds);
            var newHours = Math.Floor(character.OnlineTime.TotalHours);
            var deltaSeconds = (uint)(newSeconds - lastSeconds);

            if (newHours > lastHours)
                character.Achievements?.UpdatePlayTime(character.OnlineTime);

            // Update Account Divine Clock time
            var (time, taken) = AccountManager.Instance.GetDivineClock(character.AccountId);
            time += deltaSeconds;
            AccountManager.Instance.UpdateDivineClock(character.AccountId, time, taken);

            // TODO: Add divine clock feedback packets
        }

        lock (_lock)
        {
            Busy = false;
        }
    }
}
