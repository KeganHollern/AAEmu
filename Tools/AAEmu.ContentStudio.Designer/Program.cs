using AAEmu.ContentStudio.Designer;
using AAEmu.ContentStudio.Designer.Components;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (!args.Contains("--urls", StringComparer.OrdinalIgnoreCase))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5188");
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<DesignerWorkspace>();
var keyDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".content-studio", "designer-keys"));
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
