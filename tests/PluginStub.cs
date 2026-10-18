namespace Jellyfin.Plugin.WizarrRequests;
// The HTTP tests compile the production controller/store/client unchanged. Only the
// Jellyfin plugin loader is replaced; installation is separately tested on Jellyfin.
public sealed class Plugin
{
    public static Plugin Instance { get; } = new();
    public PluginConfiguration Configuration { get; } = new();
}
public sealed class PluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public string ServerName { get; set; } = "Synthetic server";
    public string WizarrApiUrl { get; set; } = "";
    public string WizarrPublicUrl { get; set; } = "https://invites.example.invalid";
    public string WizarrApiKey { get; set; } = "synthetic-integration-key";
    public bool PrefillUsername { get; set; }
    public bool SeerrCleanupEnabled { get; set; }
    public string SeerrApiUrl { get; set; } = "";
    public string SeerrApiKey { get; set; } = "";
    public int ServerId { get; set; } = 7;
    public int[] LibraryIds { get; set; } = [4, 5];
}
