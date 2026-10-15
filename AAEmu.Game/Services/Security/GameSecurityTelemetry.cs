using System.Net;
using AAEmu.Game.Utils.Scripts;
using NLog;

namespace AAEmu.Game.Services.Security;

public enum SecurityNetwork { Game, Stream, Login }
public enum SecurityDisconnectReason
{
    RemoteClose, ServerClose, ServerShutdown, AdmissionLimit, AuthenticationTimeout,
    AuthenticationRejected, InvalidState, RateLimit, ProtocolError, DuplicateAccount, Moderation
}

internal enum SecurityTokenKind { WorldCookie, Stream, Reconnect }
internal enum SecurityTokenFailure
{
    NotFound, AccountMismatch, Expired, AddressMismatch, AddressBlocked, InvalidState, AdmissionDenied
}
internal enum SecurityPacketRejection { UnknownOpcode, InvalidState, RateLimit, Malformed, HandlerError }

/// <summary>
/// Keeps security measurements independent from bounded event output. Event fields never include packet
/// payloads, authentication material, suspicious-activity descriptions, or command arguments.
/// </summary>
internal static class GameSecurityTelemetry
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal static void TokenFailure(SecurityTokenKind kind, SecurityTokenFailure reason,
        bool emitEvent, uint connectionId = 0, IPAddress remoteIp = null, GameSecurityMetrics metrics = null)
    {
        var tokenKind = kind switch
        {
            SecurityTokenKind.WorldCookie => "world_cookie",
            SecurityTokenKind.Stream => "stream",
            _ => "reconnect"
        };
        var tokenReason = reason switch
        {
            SecurityTokenFailure.NotFound => "not_found",
            SecurityTokenFailure.AccountMismatch => "account_mismatch",
            SecurityTokenFailure.Expired => "expired",
            SecurityTokenFailure.AddressMismatch => "address_mismatch",
            SecurityTokenFailure.AddressBlocked => "address_blocked",
            SecurityTokenFailure.InvalidState => "invalid_state",
            _ => "admission_denied"
        };
        (metrics ?? GameSecurityMetrics.Default).TokenFailure(tokenKind, tokenReason);
        if (emitEvent)
            Logger.Warn("{EventName}: Rejected {TokenKind} authentication ({Reason}) on connection {ConnectionId} from {RemoteIp}",
                "game.authentication.rejected", tokenKind, tokenReason, connectionId, remoteIp);
    }

    internal static void PacketRejected(SecurityNetwork network, SecurityPacketRejection reason,
        bool emitEvent, uint connectionId, IPAddress remoteIp, uint? opcode = null, byte? level = null,
        int? length = null, GameSecurityMetrics metrics = null)
    {
        var networkName = NetworkName(network);
        var packetReason = reason switch
        {
            SecurityPacketRejection.UnknownOpcode => "unknown_opcode",
            SecurityPacketRejection.InvalidState => "invalid_state",
            SecurityPacketRejection.RateLimit => "rate_limit",
            SecurityPacketRejection.Malformed => "malformed",
            _ => "handler_error"
        };
        (metrics ?? GameSecurityMetrics.Default).PacketRejected(networkName, packetReason);
        if (emitEvent)
            Logger.Warn("{EventName}: Rejected {Network} packet ({Reason}) with opcode {PacketOpcode} at level {PacketLevel}, " +
                "{PacketLength} unread bytes on connection {ConnectionId} from {RemoteIp}",
                "game.packet.rejected", networkName, packetReason, opcode, level, length, connectionId, remoteIp);
    }

    internal static void Disconnected(SecurityNetwork network, SecurityDisconnectReason reason,
        uint connectionId = 0, IPAddress remoteIp = null, GameSecurityMetrics metrics = null)
    {
        var networkName = NetworkName(network);
        var disconnectReason = reason switch
        {
            SecurityDisconnectReason.RemoteClose => "remote_close",
            SecurityDisconnectReason.ServerClose => "server_close",
            SecurityDisconnectReason.ServerShutdown => "server_shutdown",
            SecurityDisconnectReason.AdmissionLimit => "admission_limit",
            SecurityDisconnectReason.AuthenticationTimeout => "authentication_timeout",
            SecurityDisconnectReason.AuthenticationRejected => "authentication_rejected",
            SecurityDisconnectReason.InvalidState => "invalid_state",
            SecurityDisconnectReason.RateLimit => "rate_limit",
            SecurityDisconnectReason.ProtocolError => "protocol_error",
            SecurityDisconnectReason.DuplicateAccount => "duplicate_account",
            _ => "moderation"
        };
        (metrics ?? GameSecurityMetrics.Default).Disconnected(networkName, disconnectReason);
        Logger.Info("{EventName}: {Network} connection {ConnectionId} from {RemoteIp} closed ({Reason})",
            "game.connection.closed", networkName, connectionId, remoteIp, disconnectReason);
    }

    internal static void SuspiciousActivity(string category, string outcome, bool emitEvent, GameSecurityMetrics metrics = null)
    {
        var categoryName = category switch
        {
            "Bot" => "bot",
            "BotReport" => "bot_report",
            "Cheat" => "cheat",
            "ChatSpam" => "chat_spam",
            "RMT" => "rmt",
            _ => "other"
        };
        (metrics ?? GameSecurityMetrics.Default).SuspiciousActivity(categoryName, outcome);
        if (emitEvent)
            Logger.Warn("{EventName}: Suspicious activity category {Category} received ({Outcome})",
                "game.suspicious_activity", categoryName, outcome);
    }

    internal static void CommandOutcome(CommandAuditEntry entry, GameSecurityMetrics metrics = null)
    {
        var source = entry.Source == "game-chat" ? "game_chat" : entry.Source == "web-api" ? "web_api" : "other";
        var outcome = entry.Result switch
        {
            "completed" => "completed",
            "denied" => "denied",
            "rejected" => "rejected",
            "failed" => "failed",
            "audit-unavailable" => "audit_unavailable",
            "audit-finish-failed" => "audit_finish_failed",
            _ => "other"
        };
        (metrics ?? GameSecurityMetrics.Default).CommandOutcome(source, outcome);
        Logger.Info("{EventName}: Request={RequestId} ActorAccount={ActorAccountId} ActorCharacter={ActorCharacterId} " +
            "Role={ActorRole} Source={CommandSource} Result={CommandResult}",
            "staff.command", entry.RequestId, entry.ActorAccountId, entry.ActorCharacterId,
            entry.ActorRole, source, outcome);
    }

    private static string NetworkName(SecurityNetwork network) => network switch
    {
        SecurityNetwork.Game => "game",
        SecurityNetwork.Stream => "stream",
        _ => "login"
    };
}
