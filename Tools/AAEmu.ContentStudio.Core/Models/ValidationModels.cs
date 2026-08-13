namespace AAEmu.ContentStudio.Core.Models;

public enum ValidationSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? Source = null,
    string? Entity = null);

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; set; } = [];
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);
    public int ErrorCount => Issues.Count(issue => issue.Severity == ValidationSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == ValidationSeverity.Warning);

    public void AddError(string code, string message, string? source = null, string? entity = null)
    {
        Issues.Add(new ValidationIssue(ValidationSeverity.Error, code, message, source, entity));
    }

    public void AddWarning(string code, string message, string? source = null, string? entity = null)
    {
        Issues.Add(new ValidationIssue(ValidationSeverity.Warning, code, message, source, entity));
    }

    public void AddInformation(string code, string message, string? source = null, string? entity = null)
    {
        Issues.Add(new ValidationIssue(ValidationSeverity.Information, code, message, source, entity));
    }
}
