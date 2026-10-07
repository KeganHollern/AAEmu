namespace AAEmu.Game.Models;

/// <summary>Operational limits for the public Game port. These are server policy, not client limits.</summary>
public sealed class GameNetworkLimitsConfig
{
    public int MaxConnections { get; set; } = 1024;
    public int MaxConnectionsPerAddress { get; set; } = 16;
    public int AuthenticationTimeoutSeconds { get; set; } = 10;
    public int PacketsPerSecond { get; set; } = 1000;

    internal void Validate()
    {
        if (MaxConnections <= 0 || MaxConnectionsPerAddress <= 0 ||
            AuthenticationTimeoutSeconds <= 0 || PacketsPerSecond <= 0)
            throw new InvalidOperationException("GameNetworkLimits values must be positive.");
    }
}
