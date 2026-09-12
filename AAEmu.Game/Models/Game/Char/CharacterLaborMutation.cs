using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Char;

/// <summary>Stages labor and its rewards before the character and account rows enter the asset transaction.</summary>
public sealed class CharacterLaborMutation(Character owner) : IDisposable
{
    private readonly short _labor = owner.LaborPower;
    private readonly int _consumed = owner.ConsumedLaborPower;
    private readonly Action _restoreRewards = owner.CaptureLaborRewardState();
    private readonly List<Action> _publishRewards = [];
    private short _amount;
    private readonly List<(short Amount, uint Actability)> _additional = [];
    private bool _finished;
    internal bool HasChanges => _amount > 0 || _additional.Count > 0;

    public bool TryConsume(short amount, uint actability)
    {
        if (_amount != 0 || _finished || !owner.TryStageLaborConsumption(amount))
            return false;
        _amount = amount;
        if (amount > 0)
            _publishRewards.Add(owner.PrepareLaborRewards(amount, actability));
        return true;
    }

    internal bool TryConsumeAdditional(short amount, uint actability)
    {
        if (_finished || amount <= 0 ||
            (long)_amount + _additional.Sum(debit => debit.Amount) + amount > short.MaxValue ||
            !owner.TryStageLaborConsumption(amount))
            return false;
        _additional.Add((amount, actability));
        _publishRewards.Add(owner.PrepareLaborRewards(amount, actability));
        return true;
    }

    public void Save(PersistenceSaveContext context)
    {
        if (!HasChanges || _finished)
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
        if (_finished)
            return;
        _finished = true;
        foreach (var publish in _publishRewards)
            publish();
    }

    public void PreservePreparedState() => _finished = true;

    public void Dispose()
    {
        if (!_finished && HasChanges)
        {
            owner.RestoreStagedLabor(_labor, _consumed);
            _restoreRewards();
        }
        _finished = true;
    }
}
