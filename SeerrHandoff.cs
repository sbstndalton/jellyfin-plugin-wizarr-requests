using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WizarrRequests;

public static class SeerrHandoff
{
    public static string? PublicUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri.AbsoluteUri.TrimEnd('/') : null;

    public sealed class Input { public string Contents { get; set; } = ""; }
    public static string Transform(Input input)
    {
        var html = input.Contents;
        if (!Plugin.Instance.Configuration.SeerrHandoffEnabled || PublicUrl(Plugin.Instance.Configuration.SeerrPublicUrl) is null || html.Contains("data-wizarr-seerr-handoff")) return html;
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? html : html.Insert(end, "<script data-wizarr-seerr-handoff defer src=\"../WizarrRequests/seerr-handoff.js?v=1.2.1\"></script>");
    }
}

public sealed class SeerrHandoffRegistration(ILogger<SeerrHandoffRegistration> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var assembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x => x.GetName().Name == "Jellyfin.Plugin.FileTransformation");
                var method = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
                if (method is not null)
                {
                    var json = JsonSerializer.Serialize(new { id = "1f6ec8f6-87d0-48fd-9c53-60bc55a61524", fileNamePattern = "index.html", callbackAssembly = typeof(SeerrHandoff).Assembly.FullName, callbackClass = typeof(SeerrHandoff).FullName, callbackMethod = nameof(SeerrHandoff.Transform) });
                    var payload = method.GetParameters()[0].ParameterType.GetMethod("Parse", [typeof(string)])!.Invoke(null, [json]);
                    method.Invoke(null, [payload]);
                    logger.LogInformation("Seerr browser handoff transformation registered.");
                    return;
                }
            }
            catch (Exception) { /* Companion may still be starting; retry without affecting Jellyfin. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        logger.LogWarning("Seerr browser handoff needs the File Transformation companion; client links remain unchanged.");
    }
}
