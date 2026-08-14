using System.Text.RegularExpressions;

namespace AAEmu.ContentStudio.Designer;

/// <summary>
/// Keeps internal storage language out of designer-facing errors and validation messages.
/// The original exception remains available to logs and developer tools.
/// </summary>
public static partial class DesignerMessage
{
    public static string ForUser(Exception exception) => ForUser(exception.Message);

    public static string ForUser(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Content Studio could not complete that action. Try again or ask an agent to inspect the project.";
        }

        var friendly = IdentityPlural().Replace(message, "internal values");
        friendly = IdentitySingular().Replace(friendly, "internal value");
        friendly = DatabaseTerm().Replace(friendly, "game data");
        friendly = WindowsPath().Replace(friendly, "the configured project file");
        return friendly;
    }

    [GeneratedRegex(@"\bIDs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPlural();

    [GeneratedRegex(@"\bID\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentitySingular();

    [GeneratedRegex(@"\bdatabase\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseTerm();

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();
}
