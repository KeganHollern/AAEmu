using System.Globalization;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Utils;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace AAEmu.Game.Scripts.SubCommands.Doodads;

/// <summary>
/// Admin-only, client-private visual placement editor for authored world doodads.
/// The authoritative doodad is never mutated: a detached copy with a distinct held ObjId is
/// sent only to the editing client. Final coordinates are emitted for a reviewed source edit.
/// </summary>
public class DoodadEditSubCommand : SubCommandBase
{
    private static Logger PlacementLogger { get; } = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<uint, DoodadPlacementSession> _sessions = [];
    private readonly object _sessionsLock = new();

    public DoodadEditSubCommand()
    {
        Title = "[Doodad Edit]";
        Description = "Preview and export a world doodad transform without changing the live world object.";
        CallPrefix = $"{CommandManager.CommandPrefix}doodad edit";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args,
        IMessageOutput messageOutput)
    {
        var editor = (Character)character;
        lock (_sessionsLock)
            ExecuteLocked(editor, args, messageOutput);
    }

    private void ExecuteLocked(Character editor, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            SendUsage(messageOutput);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "select":
                SelectByObjId(editor, args, messageOutput);
                break;
            case "nearest":
                SelectNearestTemplate(editor, args, messageOutput);
                break;
            case "status":
            case "info":
            case "show":
                ShowStatus(editor, messageOutput, true);
                break;
            case "refresh":
                Refresh(editor, messageOutput);
                break;
            case "nudge":
                Nudge(editor, args, messageOutput);
                break;
            case "rotate":
            case "turn":
                Rotate(editor, args, messageOutput);
                break;
            case "set":
                SetAbsolute(editor, args, messageOutput);
                break;
            case "phases":
                ListPhases(editor, messageOutput);
                break;
            case "phase":
                PreviewPhase(editor, args, messageOutput);
                break;
            case "undo":
                Undo(editor, messageOutput);
                break;
            case "reset":
                Reset(editor, messageOutput);
                break;
            case "json":
            case "result":
                EmitResult(editor, messageOutput, false);
                break;
            case "done":
            case "commit":
                EmitResult(editor, messageOutput, true);
                break;
            case "cancel":
                Cancel(editor, messageOutput);
                break;
            default:
                SendColorMessage(messageOutput, System.Drawing.Color.Red, $"Unknown action '{args[0]}'");
                SendUsage(messageOutput);
                break;
        }
    }

    protected override void SendHelpMessage(IMessageOutput messageOutput)
    {
        SendUsage(messageOutput);
    }

    private void SendUsage(IMessageOutput messageOutput)
    {
        SendMessage(messageOutput, "Client-private, preview-only doodad placement editor:");
        SendMessage(messageOutput, "/doodad edit select <ObjId>");
        SendMessage(messageOutput, "/doodad edit nearest <TemplateId> [radius=30]");
        SendMessage(messageOutput, "/doodad edit nudge <x|y|z|scale> <delta>");
        SendMessage(messageOutput, "/doodad edit rotate <roll|pitch|yaw> <delta-degrees>");
        SendMessage(messageOutput, "/doodad edit set <x|y|z|roll|pitch|yaw|scale> <value>");
        SendMessage(messageOutput, "/doodad edit phases | phase <FuncGroupId|original>");
        SendMessage(messageOutput, "/doodad edit undo | reset | refresh | status | json | done | cancel");
        SendMessage(messageOutput,
            "done logs exact spawn JSON and restores your authoritative view; it never writes MySQL or source files.");
    }

    private void SelectByObjId(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 2 || !uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var objId))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit select <ObjId>");
            return;
        }

        var doodad = character.ParentWorld?.GetDoodad(objId);
        Select(character, doodad, messageOutput, $"ObjId {objId}");
    }

    private void SelectNearestTemplate(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length is < 2 or > 3 ||
            !uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var templateId))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit nearest <TemplateId> [radius=30]");
            return;
        }

        var radius = 30f;
        if (args.Length == 3 && (!TryParseFinite(args[2], out radius) || radius <= 0f || radius > 500f))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Radius must be a finite number greater than 0 and no more than 500");
            return;
        }

        var world = character.ParentWorld;
        if (world == null)
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red, "Character has no parent world");
            return;
        }

        var characterPosition = character.Transform.World.Position;
        var nearest = world.GetAllDoodads()
            .Where(doodad => doodad.TemplateId == templateId &&
                             IsSafeAuthoredWorldDoodad(doodad, out _) &&
                             IsVisibleTo(character, doodad))
            .Select(doodad => (Doodad: doodad,
                Distance: Vector3.Distance(characterPosition, doodad.Transform.World.Position)))
            .Where(candidate => candidate.Distance <= radius)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();

        Select(character, nearest.Doodad, messageOutput,
            $"template {templateId} within {Format(radius)} m");
    }

    private void Select(Character character, Doodad doodad, IMessageOutput messageOutput, string requested)
    {
        if (doodad == null)
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                $"No doodad found for {requested}");
            return;
        }

        if (!IsSafeAuthoredWorldDoodad(doodad, out var reason))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                $"Doodad {doodad.ObjId} cannot be preview-edited: {reason}");
            return;
        }
        if (!IsVisibleTo(character, doodad))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                $"Doodad {doodad.ObjId} is outside your current visibility region");
            return;
        }

        RestoreAndRemovePreviousSession(character);

        var original = DoodadPlacementSnapshot.Capture(doodad);
        var previewObjId = ObjectIdManager.Instance.GetNextId();
        if (previewObjId == doodad.ObjId)
        {
            ObjectIdManager.Instance.ReleaseId(previewObjId);
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Could not reserve a distinct preview object ID");
            return;
        }
        var session = new DoodadPlacementSession(
            character,
            character.ParentWorld.Id,
            doodad.ObjId,
            previewObjId,
            doodad.Guid,
            doodad.TemplateId,
            original,
            doodad.Spawner);
        session.Subscription = new DoodadPlacementSubscription(
            () => OnCharacterSubscriberDisposed(session));
        lock (_sessionsLock)
            _sessions[character.Id] = session;
        character.PushSubscriber(session.Subscription);
        character.Events.OnDungeonLeave += OnCharacterDungeonLeave;

        try
        {
            SendPreview(character, doodad, session.PreviewObjId, original);
        }
        catch (Exception exception)
        {
            try
            {
                RestoreSourceView(character, doodad, session.PreviewObjId);
            }
            catch (Exception restoreException)
            {
                PlacementLogger.Debug(restoreException,
                    $"Failed to restore doodad {doodad.ObjId} after preview creation failed");
            }
            RemoveSession(character.Id);
            PlacementLogger.Error(exception,
                $"Failed to create doodad placement preview for {doodad.TemplateId}/{doodad.ObjId}");
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Could not create the detached preview; edit session cleared");
            return;
        }
        PlacementLogger.Info(
            $"DOODAD_PLACEMENT_SESSION_START character={character.Name} characterId={character.Id} sourceObjId={doodad.ObjId} previewObjId={session.PreviewObjId}");
        SendMessage(messageOutput,
            $"Selected @DOODAD_NAME({doodad.TemplateId}) template {doodad.TemplateId}, ObjId {doodad.ObjId}; only your client sees this preview");
        SendColorMessage(messageOutput, System.Drawing.Color.Yellow,
            "Visual preview only: do not click, climb, or otherwise interact with it");
        ShowStatus(character, messageOutput, false);
    }

    private void Refresh(Character character, IMessageOutput messageOutput)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        DoodadPlacementSnapshot preview;
        lock (session)
            preview = session.Preview;
        SendPreview(character, source, session.PreviewObjId, preview);
        ShowStatus(character, messageOutput, false);
    }

    private void Nudge(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 3 || !TryParseFinite(args[2], out var delta))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit nudge <x|y|z|scale> <finite-delta>");
            return;
        }

        Mutate(character, messageOutput, current => current.Nudge(args[1], delta));
    }

    private void Rotate(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 3 || !TryParseFinite(args[2], out var deltaDegrees))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit rotate <roll|pitch|yaw> <finite-delta-degrees>");
            return;
        }

        Mutate(character, messageOutput, current => current.RotateDegrees(args[1], deltaDegrees));
    }

    private void SetAbsolute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 3 || !TryParseFinite(args[2], out var value))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit set <x|y|z|roll|pitch|yaw|scale> <finite-value>");
            return;
        }

        Mutate(character, messageOutput, current => current.SetValue(args[1], value));
    }

    private void ListPhases(Character character, IMessageOutput messageOutput)
    {
        if (!TryResolve(character, messageOutput, out _, out var doodad))
            return;

        var groups = DoodadManager.Instance.GetDoodadFuncGroups(doodad.TemplateId);
        if (groups.Count == 0)
        {
            SendMessage(messageOutput, $"Template {doodad.TemplateId} has no func groups");
            return;
        }

        foreach (var group in groups)
        {
            SendMessage(messageOutput,
                $"phase {group.Id}: kind={group.GroupKindId}, model={group.Model}, sound={group.SoundId}");
        }
    }

    private void PreviewPhase(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 2)
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Usage: /doodad edit phase <FuncGroupId|original>");
            return;
        }

        if (!TryResolve(character, messageOutput, out var session, out var doodad))
            return;

        uint funcGroupId;
        if (args[1].Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            funcGroupId = session.Original.FuncGroupId;
        }
        else if (!uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                     out funcGroupId))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "FuncGroupId must be an integer or 'original'");
            return;
        }

        var available = DoodadManager.Instance.GetDoodadFuncGroupsId(doodad.TemplateId);
        if (!available.Contains(funcGroupId))
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                $"FuncGroupId {funcGroupId} is unavailable; choose one of: {string.Join(", ", available)}");
            return;
        }

        Mutate(character, messageOutput,
            current => current with { FuncGroupId = funcGroupId }, true);
    }

    private void Undo(Character character, IMessageOutput messageOutput)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        DoodadPlacementSnapshot previous;
        lock (session)
        {
            if (!session.Undo.TryPop(out previous))
            {
                SendColorMessage(messageOutput, System.Drawing.Color.Red, "Nothing to undo");
                return;
            }

            session.Preview = previous;
        }

        SendPreview(character, source, session.PreviewObjId, previous);
        ShowStatus(character, messageOutput, false);
    }

    private void Reset(Character character, IMessageOutput messageOutput)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        lock (session)
        {
            session.Undo.Push(session.Preview);
            session.Preview = session.Original;
        }

        SendPreview(character, source, session.PreviewObjId, session.Original);
        ShowStatus(character, messageOutput, false);
    }

    private void EmitResult(Character character, IMessageOutput messageOutput, bool finish)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        DoodadPlacementSnapshot preview;
        lock (session)
            preview = session.Preview;

        var json = preview.ToPlacementJson(source.TemplateId, session.SourceSpawner.RelatedIds);
        SendMessage(messageOutput, $"Placement JSON: {json}");
        SendMessage(messageOutput,
            $"Preview phase {preview.FuncGroupId} is intentionally not exported; copy only Position/Scale into the existing source entry");
        PlacementLogger.Info(
            $"DOODAD_PLACEMENT_RESULT character={character.Name} characterId={character.Id} world={character.ParentWorld.Template?.Name} instance={character.ParentWorld.Id} objId={source.ObjId} previewPhase={preview.FuncGroupId} json={json}");

        if (!finish)
            return;

        RestoreSourceView(character, source, session.PreviewObjId);
        RemoveSession(character.Id);
        SendMessage(messageOutput,
            "Authoritative view restored and edit session cleared; no database or source file was written");
    }

    private void Cancel(Character character, IMessageOutput messageOutput)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        RestoreSourceView(character, source, session.PreviewObjId);
        RemoveSession(character.Id);
        SendMessage(messageOutput, "Authoritative view restored; edit session cleared");
    }

    private void Mutate(Character character, IMessageOutput messageOutput,
        Func<DoodadPlacementSnapshot, DoodadPlacementSnapshot> mutation,
        bool replayPhase = false)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        DoodadPlacementSnapshot previous;
        DoodadPlacementSnapshot next;
        try
        {
            lock (session)
            {
                previous = session.Preview;
                next = mutation(previous);
                if (!next.IsValid())
                {
                    SendColorMessage(messageOutput, System.Drawing.Color.Red,
                        "Result is outside packet-safe bounds; x/y must be between -32768 and 32768, z from -100 to below 4096, and scale from 0.01 to 100");
                    return;
                }
                if (!IsInsideWorld(source, next.Position))
                {
                    SendColorMessage(messageOutput, System.Drawing.Color.Red,
                        "Result is outside this world's authored x/y bounds");
                    return;
                }

                session.Undo.Push(previous);
                session.Preview = next;
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            SendColorMessage(messageOutput, System.Drawing.Color.Red, exception.Message);
            return;
        }

        try
        {
            SendPreview(character, source, session.PreviewObjId, next, replayPhase);
        }
        catch (Exception exception)
        {
            lock (session)
            {
                session.Preview = previous;
                session.Undo.TryPop(out _);
            }
            PlacementLogger.Error(exception,
                $"Failed to refresh doodad placement preview {session.PreviewObjId}");
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "Could not refresh the detached preview; edit was not applied");
            return;
        }
        ShowStatus(character, messageOutput, false);
    }

    private bool TryResolve(Character character, IMessageOutput messageOutput,
        out DoodadPlacementSession session, out Doodad doodad)
    {
        lock (_sessionsLock)
            _sessions.TryGetValue(character.Id, out session);

        if (session == null)
        {
            doodad = null;
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "No active edit; use /doodad edit select <ObjId> or /doodad edit nearest <TemplateId>");
            return false;
        }

        if (!ReferenceEquals(session.Editor, character))
        {
            RemoveSession(character.Id);
            doodad = null;
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "The edit belongs to an older character connection; stale session cleared");
            return false;
        }

        if (character.ParentWorld == null || character.ParentWorld.Id != session.InstanceId)
        {
            character.SendPacket(new SCDoodadRemovedPacket(session.PreviewObjId));
            RemoveSession(character.Id);
            doodad = null;
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "The selected doodad belongs to a different instance; edit session cleared");
            return false;
        }

        doodad = character.ParentWorld.GetDoodad(session.ObjId);
        if (doodad == null || doodad.Guid != session.SourceGuid ||
            doodad.TemplateId != session.TemplateId || !ReferenceEquals(doodad.Spawner, session.SourceSpawner) ||
            !IsSafeAuthoredWorldDoodad(doodad, out _))
        {
            character.SendPacket(new SCDoodadRemovedPacket(session.PreviewObjId));
            RemoveSession(character.Id);
            doodad = null;
            SendColorMessage(messageOutput, System.Drawing.Color.Red,
                "The selected source doodad changed or disappeared; edit session cleared");
            return false;
        }

        return true;
    }

    private void ShowStatus(Character character, IMessageOutput messageOutput, bool includeJson)
    {
        if (!TryResolve(character, messageOutput, out var session, out var source))
            return;

        DoodadPlacementSnapshot preview;
        lock (session)
            preview = session.Preview;

        SendMessage(messageOutput,
            $"Template {source.TemplateId}, ObjId {source.ObjId}: {preview.ToDisplayString()}");
        if (includeJson)
            SendMessage(messageOutput,
                $"Placement JSON: {preview.ToPlacementJson(source.TemplateId, session.SourceSpawner.RelatedIds)}");
    }

    private void OnCharacterSubscriberDisposed(DoodadPlacementSession session)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(session.Editor.Id, out var active) ||
                !ReferenceEquals(active, session))
                return;

            _sessions.Remove(session.Editor.Id);
            session.Editor.Events.OnDungeonLeave -= OnCharacterDungeonLeave;
            ObjectIdManager.Instance.ReleaseId(session.PreviewObjId);
        }
    }

    private void OnCharacterDungeonLeave(object sender, OnDungeonLeaveArgs args)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(args.Player.Id, out var session) ||
                !ReferenceEquals(session.Editor, args.Player))
                return;

            args.Player.SendPacket(new SCDoodadRemovedPacket(session.PreviewObjId));
            RemoveSession(args.Player.Id);
        }
    }

    private void RestoreAndRemovePreviousSession(Character character)
    {
        DoodadPlacementSession previous;
        lock (_sessionsLock)
            _sessions.TryGetValue(character.Id, out previous);
        if (previous == null)
            return;

        if (character.ParentWorld?.Id == previous.InstanceId)
        {
            var source = character.ParentWorld.GetDoodad(previous.ObjId);
            if (source != null && source.Guid == previous.SourceGuid &&
                source.TemplateId == previous.TemplateId && ReferenceEquals(source.Spawner, previous.SourceSpawner))
                RestoreSourceView(character, source, previous.PreviewObjId);
            else
                character.SendPacket(new SCDoodadRemovedPacket(previous.PreviewObjId));
        }
        else
        {
            character.SendPacket(new SCDoodadRemovedPacket(previous.PreviewObjId));
        }

        RemoveSession(character.Id);
    }

    private void RemoveSession(uint characterId)
    {
        DoodadPlacementSession removed;
        lock (_sessionsLock)
        {
            if (!_sessions.Remove(characterId, out removed))
                return;
        }

        removed.Editor.Events.OnDungeonLeave -= OnCharacterDungeonLeave;
        // Do not mutate Subscribers while a disconnect thread may be enumerating it.
        // A cancelled subscription is inert and is discarded with the Character.
        removed.Subscription?.Cancel();
        ObjectIdManager.Instance.ReleaseId(removed.PreviewObjId);
        PlacementLogger.Info(
            $"DOODAD_PLACEMENT_SESSION_END character={removed.Editor.Name} characterId={characterId} sourceObjId={removed.ObjId} previewObjId={removed.PreviewObjId}");
    }

    private static void SendPreview(Character character, Doodad source, uint previewObjId,
        DoodadPlacementSnapshot snapshot, bool replayPhase = false)
    {
        var preview = DoodadManager.Instance.Create(source.ParentWorld, previewObjId,
            source.TemplateId, null, true);
        if (preview == null)
            throw new InvalidOperationException($"Could not create preview for doodad {source.TemplateId}");

        snapshot.ApplyTo(preview);
        preview.OwnerId = source.OwnerId;
        preview.OwnerObjId = source.OwnerObjId;
        preview.ParentObjId = source.ParentObjId;
        preview.AttachPoint = source.AttachPoint;
        preview.OwnerType = source.OwnerType;
        preview.OwnerDbId = source.OwnerDbId;
        preview.UccId = source.UccId;
        preview.ItemTemplateId = source.ItemTemplateId;
        preview.PlantTime = source.PlantTime;
        preview.GrowthTime = source.GrowthTime;
        if (snapshot.FuncGroupId == source.FuncGroupId)
            preview.PhaseTime = source.PhaseTime;
        preview.QuestGlow = source.QuestGlow;
        preview.PuzzleGroup = source.PuzzleGroup;
        preview.Data = source.Data;

        character.SendPacket(new SCDoodadRemovedPacket(source.ObjId));
        character.SendPacket(new SCDoodadRemovedPacket(previewObjId));
        character.SendPacket(new SCDoodadCreatedPacket(preview));
        if (replayPhase)
            character.SendPacket(new SCDoodadPhaseChangedPacket(preview));
    }

    private static void RestoreSourceView(Character character, Doodad source, uint previewObjId)
    {
        character.SendPacket(new SCDoodadRemovedPacket(previewObjId));
        if (!IsVisibleTo(character, source))
            return;

        character.SendPacket(new SCDoodadRemovedPacket(source.ObjId));
        character.SendPacket(new SCDoodadCreatedPacket(source));
    }

    private static bool IsVisibleTo(Character character, Doodad doodad)
    {
        return doodad.IsVisible && character.ParentWorld == doodad.ParentWorld &&
               WorldManager.GetAround<Doodad>(character).Any(candidate => ReferenceEquals(candidate, doodad));
    }

    internal static bool IsSafeAuthoredWorldDoodad(Doodad doodad, out string reason)
    {
        var hasAuthoredSpawner = doodad?.Spawner != null && doodad.ParentWorld?.SpawnManager != null &&
                                 doodad.ParentWorld.SpawnManager.IsAuthoredDoodadSpawner(doodad.Spawner);
        return IsSafeAuthoredWorldDoodad(doodad, hasAuthoredSpawner, out reason);
    }

    internal static bool IsSafeAuthoredWorldDoodad(Doodad doodad, bool hasAuthoredSpawner,
        out string reason)
    {
        if (doodad == null)
        {
            reason = "it does not exist";
            return false;
        }
        if (doodad.IsPersistent || doodad.DbId != 0)
        {
            reason = "it is persistent or database-backed";
            return false;
        }
        if (!hasAuthoredSpawner || doodad.Spawner == null ||
            doodad.Spawner.UnitId != doodad.TemplateId ||
            !ReferenceEquals(doodad.Spawner.ParentWorld, doodad.ParentWorld))
        {
            reason = "its spawner is not in this instance's authored world registry";
            return false;
        }
        if (doodad.OwnerType != DoodadOwnerType.System || doodad.OwnerId != 0 ||
            doodad.OwnerObjId != 0 || doodad.OwnerDbId != 0)
        {
            reason = "it is owned rather than a system world spawn";
            return false;
        }
        if (doodad.ParentObjId != 0 ||
            doodad.AttachPoint is not (AttachPointKind.None or AttachPointKind.System) ||
            doodad.Transform.Parent != null || doodad.Transform.StickyParent != null)
        {
            reason = "it is attached or parented";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsInsideWorld(Doodad source, Vector3 position)
    {
        var template = source.ParentWorld?.Template;
        return template != null && template.CellX > 0 && template.CellY > 0 &&
               position.X >= 0f && position.Y >= 0f &&
               position.X < template.CellX * WorldManager.CELL_SIZE &&
               position.Y < template.CellY * WorldManager.CELL_SIZE;
    }

    private static bool TryParseFinite(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               float.IsFinite(value);
    }

    private static string Format(float value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

internal sealed class DoodadPlacementSession
{
    public DoodadPlacementSession(Character editor, uint instanceId, uint objId, uint previewObjId,
        Guid sourceGuid, uint templateId, DoodadPlacementSnapshot original, DoodadSpawner sourceSpawner)
    {
        if (objId == previewObjId)
            throw new ArgumentException("Detached preview ObjId must differ from the authoritative doodad ObjId",
                nameof(previewObjId));

        Editor = editor;
        InstanceId = instanceId;
        ObjId = objId;
        PreviewObjId = previewObjId;
        SourceGuid = sourceGuid;
        TemplateId = templateId;
        Original = original;
        Preview = original;
        SourceSpawner = sourceSpawner;
    }

    public Character Editor { get; }
    public uint InstanceId { get; }
    public uint ObjId { get; }
    public uint PreviewObjId { get; }
    public Guid SourceGuid { get; }
    public uint TemplateId { get; }
    public DoodadPlacementSnapshot Original { get; }
    public DoodadPlacementSnapshot Preview { get; set; }
    public DoodadSpawner SourceSpawner { get; }
    public DoodadPlacementSubscription Subscription { get; set; }
    public Stack<DoodadPlacementSnapshot> Undo { get; } = new();
}

internal sealed class DoodadPlacementSubscription(Action onDisposed) : IDisposable
{
    private Action _onDisposed = onDisposed;

    public void Cancel()
    {
        Interlocked.Exchange(ref _onDisposed, null);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
    }
}

internal readonly record struct DoodadPlacementSnapshot(
    Vector3 Position,
    Vector3 Rotation,
    float Scale,
    uint FuncGroupId)
{
    private const float MinimumScale = 0.01f;
    private const float MaximumScale = 100f;
    private const float MinimumHorizontalPosition = -32768f;
    private const float MaximumHorizontalPosition = 32768f;
    private const float MinimumVerticalPosition = -100f;
    private const float MaximumVerticalPosition = 4096f;

    public static DoodadPlacementSnapshot Capture(Doodad doodad)
    {
        return new DoodadPlacementSnapshot(
            doodad.Transform.World.Position,
            doodad.Transform.World.Rotation,
            doodad.Scale,
            doodad.FuncGroupId);
    }

    public DoodadPlacementSnapshot Nudge(string axis, float delta)
    {
        return axis.ToLowerInvariant() switch
        {
            "x" => this with { Position = new Vector3(Position.X + delta, Position.Y, Position.Z) },
            "y" => this with { Position = new Vector3(Position.X, Position.Y + delta, Position.Z) },
            "z" or "up" => this with { Position = new Vector3(Position.X, Position.Y, Position.Z + delta) },
            "scale" => this with { Scale = Scale + delta },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Nudge axis must be x, y, z, or scale")
        };
    }

    public DoodadPlacementSnapshot RotateDegrees(string axis, float deltaDegrees)
    {
        return axis.ToLowerInvariant() switch
        {
            "roll" or "r" => this with
            {
                Rotation = new Vector3(
                    NormalizeDegrees(Rotation.X.RadToDeg() + deltaDegrees).DegToRad(),
                    Rotation.Y,
                    Rotation.Z)
            },
            "pitch" or "p" => this with
            {
                Rotation = new Vector3(
                    Rotation.X,
                    NormalizeDegrees(Rotation.Y.RadToDeg() + deltaDegrees).DegToRad(),
                    Rotation.Z)
            },
            "yaw" => this with
            {
                Rotation = new Vector3(
                    Rotation.X,
                    Rotation.Y,
                    NormalizeDegrees(Rotation.Z.RadToDeg() + deltaDegrees).DegToRad())
            },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Rotation axis must be roll, pitch, or yaw")
        };
    }

    public DoodadPlacementSnapshot SetValue(string axis, float value)
    {
        return axis.ToLowerInvariant() switch
        {
            "x" => this with { Position = new Vector3(value, Position.Y, Position.Z) },
            "y" => this with { Position = new Vector3(Position.X, value, Position.Z) },
            "z" => this with { Position = new Vector3(Position.X, Position.Y, value) },
            "roll" or "r" => this with
            {
                Rotation = new Vector3(NormalizeDegrees(value).DegToRad(), Rotation.Y, Rotation.Z)
            },
            "pitch" or "p" => this with
            {
                Rotation = new Vector3(Rotation.X, NormalizeDegrees(value).DegToRad(), Rotation.Z)
            },
            "yaw" => this with
            {
                Rotation = new Vector3(Rotation.X, Rotation.Y, NormalizeDegrees(value).DegToRad())
            },
            "scale" => this with { Scale = value },
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis,
                "Set axis must be x, y, z, roll, pitch, yaw, or scale")
        };
    }

    public bool IsValid()
    {
        return float.IsFinite(Position.X) && Position.X is > MinimumHorizontalPosition and < MaximumHorizontalPosition &&
               float.IsFinite(Position.Y) && Position.Y is > MinimumHorizontalPosition and < MaximumHorizontalPosition &&
               float.IsFinite(Position.Z) && Position.Z is >= MinimumVerticalPosition and < MaximumVerticalPosition &&
               float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) &&
               float.IsFinite(Scale) && Scale is >= MinimumScale and <= MaximumScale;
    }

    public void ApplyTo(Doodad doodad)
    {
        doodad.Transform.Local.SetPosition(Position, Rotation);
        doodad.SetScale(Scale);
        if (doodad.FuncGroupId != FuncGroupId)
            doodad.FuncGroupId = FuncGroupId;
    }

    public string ToDisplayString()
    {
        return $"x={Format(Position.X)}, y={Format(Position.Y)}, z={Format(Position.Z)}, " +
               $"roll={Format(Rotation.X.RadToDeg())}°, pitch={Format(Rotation.Y.RadToDeg())}°, " +
               $"yaw={Format(Rotation.Z.RadToDeg())}°, scale={Format(Scale)}, phase={FuncGroupId}";
    }

    public string ToPlacementJson(uint templateId, IReadOnlyList<uint> relatedIds)
    {
        var root = new JObject
        {
            ["UnitId"] = new JValue(templateId),
            ["Position"] = new JObject
            {
                ["X"] = new JValue(Position.X),
                ["Y"] = new JValue(Position.Y),
                ["Z"] = new JValue(Position.Z),
                ["Roll"] = new JValue(Rotation.X.RadToDeg()),
                ["Pitch"] = new JValue(Rotation.Y.RadToDeg()),
                ["Yaw"] = new JValue(Rotation.Z.RadToDeg())
            }
        };

        if (relatedIds is { Count: > 0 })
            root["RelatedIds"] = new JArray(relatedIds);
        root["Scale"] = new JValue(Scale);

        return root.ToString(Formatting.None);
    }

    private static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        if (normalized >= 180f)
            normalized -= 360f;
        if (normalized < -180f)
            normalized += 360f;
        return normalized;
    }

    private static string Format(float value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
