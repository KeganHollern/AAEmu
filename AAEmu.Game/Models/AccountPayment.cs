namespace AAEmu.Game.Models;

public class AccountPayment
{
    private readonly TimeProvider _timeProvider;
    public PaymentMethodType Method => PremiumState ? PaymentMethodType.Premium : PaymentMethodType.None;
    public int Location => 1;
    public DateTime StartTime { get; }
    public DateTime EndTime { get; }
    public bool PremiumState => IsPremiumAt(_timeProvider.GetUtcNow().UtcDateTime);

    public AccountPayment(ulong start = 0, ulong end = 0, TimeProvider timeProvider = null)
    {
        if (!ValidPeriod(start, end))
            throw new ArgumentOutOfRangeException(nameof(end), "Invalid patron period");
        _timeProvider = timeProvider ?? TimeProvider.System;
        StartTime = DateTimeOffset.FromUnixTimeSeconds((long)start).UtcDateTime;
        EndTime = DateTimeOffset.FromUnixTimeSeconds((long)end).UtcDateTime;
    }

    internal static bool ValidPeriod(ulong start, ulong end) =>
        (start == 0 && end == 0) || (start < end && end <= 253402300799UL);

    public bool IsPremiumAt(DateTime now) => StartTime < EndTime && now >= StartTime && now < EndTime;
}

public enum PaymentMethodType
{
    Premium = 1,
    Demo = 3,
    None = 5
}
