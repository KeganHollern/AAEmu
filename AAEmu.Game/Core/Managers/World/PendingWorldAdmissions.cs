using System.Net;

using AAEmu.Game.Models;

namespace AAEmu.Game.Core.Managers.World;

internal enum PendingWorldAccountResult
{
    NotFound,
    AccountMismatch,
    Expired,
    AddressMismatch,
    AddressBlocked,
    Consumed
}

/// <summary>
/// Holds one short-lived, single-use world cookie per account. The private
/// Login connection supplies the account, cookie, entitlement, and address.
/// </summary>
internal sealed class PendingWorldAdmissions(TimeProvider clock = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    internal const int FailureLimit = 3;
    internal const int MaxFailureAddresses = 4096;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, Admission> _tokens = [];
    private readonly Dictionary<uint, uint> _accounts = [];
    private readonly Dictionary<IPAddress, Failure> _failures = [];

    private sealed record Admission(uint AccountId, AccountPayment Payment, IPAddress Address, DateTimeOffset Expires);
    private sealed record Failure(int Count, DateTimeOffset Expires);

    internal bool TryAdd(uint token, uint accountId, AccountPayment payment, IPAddress address)
    {
        address = Normalize(address);
        if (token == 0 || accountId == 0 || address == null)
            return false;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            Prune(now);
            // A random collision must not overwrite another account's secret.
            if (_tokens.ContainsKey(token))
                return false;
            if (_accounts.TryGetValue(accountId, out var previous))
                RemoveLocked(previous);
            _tokens.Add(token, new Admission(accountId, payment ?? new AccountPayment(), address, now + Lifetime));
            _accounts[accountId] = token;
            return true;
        }
    }

    internal void Remove(uint token)
    {
        lock (_lock)
            RemoveLocked(token);
    }

    internal PendingWorldAccountResult Consume(uint token, uint accountId, IPAddress address,
        bool checkAddress, out AccountPayment payment)
    {
        payment = null;
        address = Normalize(address);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (address != null && _failures.TryGetValue(address, out var failure))
            {
                if (failure.Expires <= now)
                    _failures.Remove(address);
                else if (failure.Count >= FailureLimit)
                    return PendingWorldAccountResult.AddressBlocked;
            }

            PendingWorldAccountResult result;
            if (token == 0 || accountId == 0 || !_tokens.TryGetValue(token, out var admission))
                result = PendingWorldAccountResult.NotFound;
            else if (admission.Expires <= now)
            {
                RemoveLocked(token);
                result = PendingWorldAccountResult.Expired;
            }
            else if (admission.AccountId != accountId)
                result = PendingWorldAccountResult.AccountMismatch;
            else if (checkAddress && (address == null || !admission.Address.Equals(address)))
                result = PendingWorldAccountResult.AddressMismatch;
            else
            {
                RemoveLocked(token);
                payment = admission.Payment;
                return PendingWorldAccountResult.Consumed;
            }

            if (address != null)
                RecordFailure(address, now);
            return result;
        }
    }

    private void RecordFailure(IPAddress address, DateTimeOffset now)
    {
        if (_failures.TryGetValue(address, out var failure))
        {
            _failures[address] = failure with { Count = failure.Count + 1 };
            return;
        }
        foreach (var expired in _failures.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            _failures.Remove(expired);
        if (_failures.Count >= MaxFailureAddresses)
            _failures.Remove(_failures.MinBy(entry => entry.Value.Expires).Key);
        _failures.Add(address, new Failure(1, now + Lifetime));
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var token in _tokens.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            RemoveLocked(token);
        foreach (var address in _failures.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            _failures.Remove(address);
    }

    private void RemoveLocked(uint token)
    {
        if (_tokens.Remove(token, out var admission))
            _accounts.Remove(admission.AccountId);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address == null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
