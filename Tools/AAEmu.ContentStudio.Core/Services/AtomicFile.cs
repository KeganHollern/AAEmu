using System.Text;

namespace AAEmu.ContentStudio.Core.Services;

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ContentStudioException($"Unable to determine output directory for {fullPath}.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void ReplaceFrom(string sourcePath, string destinationPath)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ContentStudioException($"Unable to determine output directory for {destinationPath}.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, true);
            File.Move(temporaryPath, fullDestinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
