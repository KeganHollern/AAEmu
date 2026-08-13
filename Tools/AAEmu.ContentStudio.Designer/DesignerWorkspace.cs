using AAEmu.ContentStudio.Core.Models;
using AAEmu.ContentStudio.Core.Services;

namespace AAEmu.ContentStudio.Designer;

public sealed class DesignerWorkspace
{
    public DesignerWorkspace(IWebHostEnvironment environment)
    {
        ConfigurationPath = Environment.GetEnvironmentVariable("AAEMU_CONTENT_CONFIG")
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "Content", "content-studio.json"));
    }

    public string ConfigurationPath { get; set; }

    public StudioConfiguration LoadConfiguration() => new ProjectRepository().LoadConfiguration(ConfigurationPath);
}
