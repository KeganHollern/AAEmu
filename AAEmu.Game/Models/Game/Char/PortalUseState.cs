namespace AAEmu.Game.Models.Game.Char;

internal sealed class PortalUseState
{
    // r208022 391827f0: portals cannot be reused for 10,000 ms after teleport completion.
    internal static readonly TimeSpan ReuseDelay = TimeSpan.FromSeconds(10);
    private readonly Lock _sync = new();
    private DateTimeOffset _availableAt;
    private bool _teleportPending;

    internal bool TryBeginTeleport(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_teleportPending || now < _availableAt)
                return false;

            _teleportPending = true;
            _availableAt = now + ReuseDelay;
            return true;
        }
    }

    internal void CompleteTeleport(DateTimeOffset now)
    {
        lock (_sync)
        {
            _teleportPending = false;
            var availableAt = now + ReuseDelay;
            if (availableAt > _availableAt)
                _availableAt = availableAt;
        }
    }
}
