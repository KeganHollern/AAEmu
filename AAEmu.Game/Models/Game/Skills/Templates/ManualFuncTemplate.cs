using System.Collections.ObjectModel;
using System.Globalization;

namespace AAEmu.Game.Models.Game.Skills.Templates;

public sealed class ManualFuncTemplate
{
    public uint Id { get; }
    public ReadOnlyCollection<int> Values { get; }

    private ManualFuncTemplate(uint id, int[] values)
    {
        Id = id;
        Values = Array.AsReadOnly(values);
    }

    public static ManualFuncTemplate Parse(uint id, string valueList)
    {
        var parts = valueList.Split(';');
        var values = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            values[index] = int.Parse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture);

        return new ManualFuncTemplate(id, values);
    }

    public int GetValue(int index)
    {
        return Values[Math.Clamp(index, 0, Values.Count - 1)];
    }
}
