namespace AAEmu.Game.Models.Game.Skills;

/// <summary>
/// Tracks the single server-authorized follow-up in a client-driven combo chain.
/// A follow-up can be consumed once and only before its compact-defined deadline.
/// </summary>
public sealed class SkillComboState
{
    private readonly object _lock = new();
    private uint _skillId;
    private DateTime _expiresAtUtc;

    public void Arm(uint skillId, int durationMilliseconds, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            if (skillId == 0 || durationMilliseconds <= 0)
            {
                ClearUnsafe();
                return;
            }

            _skillId = skillId;
            _expiresAtUtc = (nowUtc ?? DateTime.UtcNow).AddMilliseconds(durationMilliseconds);
        }
    }

    public bool TryConsume(uint skillId, DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (_skillId == 0 || now > _expiresAtUtc)
            {
                ClearUnsafe();
                return false;
            }

            if (_skillId != skillId) return false;
            ClearUnsafe();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock) ClearUnsafe();
    }

    private void ClearUnsafe()
    {
        _skillId = 0;
        _expiresAtUtc = default;
    }
}
