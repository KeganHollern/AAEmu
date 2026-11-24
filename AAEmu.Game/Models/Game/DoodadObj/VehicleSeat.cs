using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj;

/// <summary>
/// Here is the work with the places in the cart
/// </summary>

public class VehicleSeat(BaseUnit parentVehicle)
{
    // objId Doodad - Chair, bench, bed where we sit down or lay
    // List<character.Id> List of employed places on a chair, bench, beds, or 0, if the place is free
    private readonly Dictionary<uint, List<uint>> _seats = []; // objId, List<character.Id>

    // Space = 1-means that there is one place (a chair), Space = 2-means that there are two places to sit (a bench on transport)
    // Spot = 0 sit left, = 1 sit right on the bench

    private void Init(uint objId, int space)
    {
        var tmp = new List<uint>();
        for (var i = 0; i < space; i++)
        {
            tmp.Add(0); // No one took a place
        }
        _seats.Add(objId, tmp); // Add a list with empty places
    }

    private void AddSeat(Character character, uint seatObjId, int spot)
    {
        _seats[seatObjId][spot] = character.Id; // occupied place
        character.Transform.Parent = null;
        character.Transform.StickyParent = parentVehicle.Transform;
    }

    public void UnLoadPassenger(Character character, uint seatObjId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            for (var i = 0; i < _seats[seatObjId].Count; i++)
            {
                if (_seats[seatObjId][i] == character.Id)
                {
                    _seats[seatObjId][i] = 0; // free up space
                    character.Transform.StickyParent = null;
                    if (parentVehicle is Transfer transfer)
                        transfer.AttachedCharacters.Remove(character);
                }
            }
        }
    }

    // The caller holds the same lock as doodad use and passenger changes.
    internal int GetFreeSeat(uint seatObjId, int space)
    {
        if (!_seats.TryGetValue(seatObjId, out var seats))
            return space > 0 ? 0 : -1;

        for (var i = 0; i < Math.Min(space, seats.Count); i++)
            if (seats[i] == 0)
                return i;

        return -1;
    }

    public int LoadPassenger(Character character, uint seatObjId, int space)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var spot = GetFreeSeat(seatObjId, space);
            if (spot < 0)
                return -1;
            if (!_seats.ContainsKey(seatObjId))
                Init(seatObjId, space);

            AddSeat(character, seatObjId, spot);
            if (parentVehicle is Transfer transfer && !transfer.AttachedCharacters.Contains(character))
                transfer.AttachedCharacters.Add(character);

            return spot;
        }
    }
}
