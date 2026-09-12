using System.Text;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/').Trim().ToLowerInvariant();
        var separator = path.IndexOf("://", StringComparison.Ordinal);
        var start = separator < 0 ? 0 : separator + 3;
        var result = new StringBuilder(path.Length);
        result.Append(path.AsSpan(0, start));
        var previousSeparator = false;
        foreach (var character in path.AsSpan(start))
        {
            // Native XlNormalizePath33017a00 ->33018010 preserves one trailing separator.
            if (character != '/' || !previousSeparator)
                result.Append(character);
            previousSeparator = character == '/';
        }
        return result.ToString();
    }
}
