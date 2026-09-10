using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Defers save bookkeeping until the transaction owner has committed all participants.
/// The persistence gate must cover writes, commit, and acknowledgement.
/// </summary>
public sealed class PersistenceSaveContext(MySqlConnection connection, MySqlTransaction transaction)
{
    private readonly List<Action> _afterCommit = [];
    private bool _acknowledged;

    public MySqlConnection Connection { get; } = connection;
    public MySqlTransaction Transaction { get; } = transaction;

    /// <summary>
    /// Enlists save bookkeeping only. Gameplay callbacks and packets belong after the
    /// complete feature operation has committed and installed its live state.
    /// </summary>
    public void AfterCommit(Action acknowledge)
    {
        ArgumentNullException.ThrowIfNull(acknowledge);
        if (_acknowledged)
            throw new InvalidOperationException("This save has already been acknowledged.");
        _afterCommit.Add(acknowledge);
    }

    internal void AcknowledgeCommit()
    {
        if (_acknowledged)
            return;
        _acknowledged = true;
        foreach (var acknowledge in _afterCommit)
            acknowledge();
        _afterCommit.Clear();
    }
}
