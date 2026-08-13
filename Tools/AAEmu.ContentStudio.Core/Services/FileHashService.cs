using System.Security.Cryptography;

namespace AAEmu.ContentStudio.Core.Services;

public static class FileHashService
{
    public static string CalculateSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static Dictionary<string, string> CalculateSha256(IEnumerable<string> paths, string relativeTo)
    {
        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(relativeTo, path).Replace('\\', '/'),
                CalculateSha256,
                StringComparer.OrdinalIgnoreCase);
    }
}
