using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Shipyard;

public sealed partial class Shipyard
{
    internal ulong SourceDesignItemId { get; set; }
    internal bool Retiring { get; set; }
    internal bool Retired { get; set; }
    internal DateTime CeremonyEnd { get; set; }
    internal ulong CompletionItemId { get; set; }

    internal void RestoreConstruction(int step, int action, bool ceremony)
    {
        if (step < -1 || (step >= 0 && !Template.ShipyardSteps.ContainsKey(step)) || action < 0 ||
            (step >= 0 && action >= Template.ShipyardSteps[step].NumActions) || (step == -1 && action != 0) ||
            (ceremony && step != -1))
            throw new InvalidOperationException("The saved shipyard construction state is invalid.");
        CurrentStep = step;
        NumAction = action;
        ShipyardData.Actions = step == -1 ? AllAction : CurrentAction;
        ShipyardData.Step = ceremony ? 1000 : step == -1 ? Template.ShipyardSteps.Count : step;
    }

    internal void Save(PersistenceSaveContext context)
    {
        if (Retiring || Retired)
            return;
        if (ShipyardData.Id == 0 || ParentWorld == null)
            throw new InvalidOperationException("A shipyard needs a durable ID and world before it can be saved.");
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = "REPLACE INTO shipyards " +
            "(id,template_id,owner_id,owner_name,faction_id,world_id,instance_id,x,y,z,yaw,spawned,hp,mp,state_hp," +
            "current_step,current_action,ceremony_end,completion_item_id) " +
            "VALUES(@id,@template,@owner,@name,@faction,@world,@instance,@x,@y,@z,@yaw,@spawned,@hp,@mp,@state_hp," +
            "@step,@action,@ceremony,@item)";
        command.Parameters.AddWithValue("@id", ShipyardData.Id);
        command.Parameters.AddWithValue("@template", ShipyardData.TemplateId);
        command.Parameters.AddWithValue("@owner", ShipyardData.Type2);
        command.Parameters.AddWithValue("@name", ShipyardData.OwnerName);
        command.Parameters.AddWithValue("@faction", (uint)ShipyardData.Type3);
        command.Parameters.AddWithValue("@world", ParentWorld.Template.Id);
        command.Parameters.AddWithValue("@instance", ParentWorld.Id);
        command.Parameters.AddWithValue("@x", ShipyardData.X);
        command.Parameters.AddWithValue("@y", ShipyardData.Y);
        command.Parameters.AddWithValue("@z", ShipyardData.Z);
        command.Parameters.AddWithValue("@yaw", ShipyardData.zRot);
        command.Parameters.AddWithValue("@spawned", ShipyardData.Spawned);
        command.Parameters.AddWithValue("@hp", Hp);
        command.Parameters.AddWithValue("@mp", Mp);
        command.Parameters.AddWithValue("@state_hp", ShipyardData.Hp);
        command.Parameters.AddWithValue("@step", CurrentStep);
        command.Parameters.AddWithValue("@action", NumAction);
        command.Parameters.AddWithValue("@ceremony", CeremonyEnd == DateTime.MinValue ? DBNull.Value : CeremonyEnd);
        command.Parameters.AddWithValue("@item", CompletionItemId);
        if (command.ExecuteNonQuery() <= 0)
            throw new InvalidOperationException("The shipyard row was not saved.");
        context.AfterCommit(() => IsDirty = false);
    }
}
