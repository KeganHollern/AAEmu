using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Char;

/// <summary>Stages the cached labor debit and writes the account row in the asset transaction.</summary>
public sealed class CharacterLaborMutation(Character owner) : IDisposable
{
    private readonly short _labor = owner.LaborPower;
    private readonly int _consumed = owner.ConsumedLaborPower;
    private short _amount;
    private uint _actability;
    private bool _finished;

    public bool TryConsume(short amount, uint actability)
    {
        if (_amount != 0 || _finished || !owner.TryStageLaborConsumption(amount))
            return false;
        _amount = amount;
        _actability = actability;
        return true;
    }

    public void Save(PersistenceSaveContext context)
    {
        if (_amount <= 0 || _finished)
            throw new InvalidOperationException("No labor debit was staged.");
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = "UPDATE accounts SET labor=@after WHERE account_id=@id AND labor=@before";
        command.Parameters.AddWithValue("@id", owner.AccountId);
        command.Parameters.AddWithValue("@before", _labor);
        command.Parameters.AddWithValue("@after", owner.LaborPower);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The account labor changed before the settlement.");
    }

    public void Complete()
    {
        _finished = true;
        owner.CompleteStagedLaborConsumption(_amount, _actability);
    }

    public void PreservePreparedState() => _finished = true;

    public void Dispose()
    {
        if (!_finished && _amount > 0)
            owner.RestoreStagedLabor(_labor, _consumed);
        _finished = true;
    }
}
