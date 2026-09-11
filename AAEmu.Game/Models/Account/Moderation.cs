using System.Text;
using AAEmu.Commons.Network;

namespace AAEmu.Game.Models.Account;

public enum ModerationAction : byte
{
    ReadState = 0,
    Ban = 1,
    Unban = 2,
    Mute = 3,
    Unmute = 4
}

public enum ModerationStatus : byte
{
    Success = 0,
    InvalidRequest = 1,
    TargetNotFound = 2,
    Unavailable = 3,
    RequestConflict = 4
}

public sealed record ModerationState(uint AccountId, ulong Revision, bool Banned, ulong BanUntil,
    bool Muted, ulong MuteUntil)
{
    public bool IsBanned(DateTimeOffset now) => IsActive(Banned, BanUntil, now);
    public bool IsMuted(DateTimeOffset now) => IsActive(Muted, MuteUntil, now);

    private static bool IsActive(bool active, ulong expiry, DateTimeOffset now)
        => active && (expiry == 0 || expiry > (ulong)now.ToUnixTimeSeconds());

    public static ModerationState Read(PacketStream stream)
    {
        var accountId = stream.ReadUInt32();
        var revision = stream.ReadUInt64();
        var banned = ReadFlag(stream);
        var banUntil = stream.ReadUInt64();
        var muted = ReadFlag(stream);
        var muteUntil = stream.ReadUInt64();
        if (accountId == 0)
            throw new InvalidDataException("Moderation state requires a positive account ID.");
        return new ModerationState(accountId, revision, banned, banUntil, muted, muteUntil);
    }

    private static bool ReadFlag(PacketStream stream)
    {
        return stream.ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid moderation Boolean.")
        };
    }
}

public sealed record ModerationRequest(ulong RequestId, uint ActorAccountId, uint ActorCharacterId,
    uint TargetAccountId, ModerationAction Action, ulong DurationSeconds, string Reason)
{
    public const int MaximumReasonBytes = 512;
    public const ulong MaximumDurationSeconds = 315360000;

    public bool IsValid()
    {
        if (RequestId == 0 || TargetAccountId == 0 || !Enum.IsDefined(Action) || Reason == null)
            return false;
        if (Action == ModerationAction.ReadState)
            return ActorAccountId == 0 && ActorCharacterId == 0 && DurationSeconds == 0 && Reason.Length == 0;
        return ActorAccountId > 0 && ActorCharacterId > 0 &&
            !string.IsNullOrWhiteSpace(Reason) && !Reason.Any(char.IsControl) &&
            Encoding.UTF8.GetByteCount(Reason) <= MaximumReasonBytes &&
            DurationSeconds <= MaximumDurationSeconds &&
            (Action is ModerationAction.Ban or ModerationAction.Mute || DurationSeconds == 0);
    }
}

public sealed record ModerationResult(ulong RequestId, ModerationStatus Status, ModerationState State);

public sealed record ModerationTarget(uint AccountId, uint CharacterId, string Name);
