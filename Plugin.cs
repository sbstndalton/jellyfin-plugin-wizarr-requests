using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WizarrRequests;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Wizarr Requests";
    public override string Description => "Request access through a profile wizard; administrators approve individual Wizarr invitations.";
    public override Guid Id => Guid.Parse("e5730687-b31f-4fc3-9bbb-322151b57917");
    public IEnumerable<PluginPageInfo> GetPages() => [new() {
        Name = "wizarrrequests", EmbeddedResourcePath = "Jellyfin.Plugin.WizarrRequests.Web.admin.html",
        EnableInMainMenu = false
    }];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string ServerName { get; set; } = "Jellyfin";
    public string WizarrApiUrl { get; set; } = "";
    public string WizarrPublicUrl { get; set; } = "";
    public string WizarrApiKey { get; set; } = "";
    public bool PrefillUsername { get; set; }
    public bool SeerrCleanupEnabled { get; set; }
    public string SeerrApiUrl { get; set; } = "";
    public string SeerrApiKey { get; set; } = "";
    public bool SeerrHandoffEnabled { get; set; }
    public string SeerrPublicUrl { get; set; } = "";
    public int ServerId { get; set; } = 1;
    public int[] LibraryIds { get; set; } = [];
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<RequestStore>(_ => new RequestStore(Plugin.Instance.DataFolderPath));
        services.AddSingleton<WizarrClient>();
        services.AddSingleton(_ => new SeerrCleanupStore(Plugin.Instance.DataFolderPath));
        services.AddSingleton<SeerrCleanupClient>();
        services.AddSingleton<SeerrCleanupProcessor>();
        services.AddScoped<MediaBrowser.Controller.Events.IEventConsumer<Jellyfin.Data.Events.Users.UserDeletedEventArgs>, UserDeletedConsumer>();
        services.AddHostedService<SeerrCleanupWorker>();
        services.AddHostedService<SeerrHandoffRegistration>();
    }
}
