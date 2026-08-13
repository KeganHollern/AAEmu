using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class DoctorService
{
    public ValidationReport Diagnose(StudioConfiguration configuration)
    {
        var report = new ValidationReport();
        CheckFile(configuration.BaselinePath, "doctor.baseline", "Baseline compact database", report);
        CheckFile(configuration.BaselineDescriptorPath, "doctor.descriptor", "Baseline descriptor", report);
        CheckFile(configuration.ProjectPath, "doctor.project", "Content project", report);

        if (!report.IsValid)
        {
            return report;
        }

        var repository = new ProjectRepository();
        var descriptor = repository.LoadBaseline(configuration.BaselineDescriptorPath);
        foreach (var issue in new BaselineVerifier().Verify(configuration.BaselinePath, descriptor).Issues)
        {
            report.Issues.Add(issue);
        }
        var project = repository.LoadProject(configuration.ProjectPath);
        foreach (var issue in new ContentValidator().ValidateProject(project, configuration.BaselinePath).Issues)
        {
            report.Issues.Add(issue);
        }
        if (report.IsValid)
        {
            Directory.CreateDirectory(Path.GetFullPath(configuration.OutputDirectory));
            report.AddInformation("doctor.ready", "Content Studio is configured and ready to build.");
        }
        return report;
    }

    private static void CheckFile(string path, string code, string name, ValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.GetFullPath(path)))
        {
            report.AddError(code, $"{name} was not found: {path}");
        }
    }
}
