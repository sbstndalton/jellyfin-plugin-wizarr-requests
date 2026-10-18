using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WizarrRequests;

public sealed record CleanupJob(Guid UserId, string State = "pending", int Attempts = 0, DateTimeOffset? RetryAfter = null);

public sealed class SeerrCleanupStore
{
    private readonly string _folder;
    private readonly object _gate = new();
    public SeerrCleanupStore(string folder) { _folder = Path.Combine(folder, "seerr-cleanup"); Directory.CreateDirectory(_folder); }
    private string PathFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".json");
    public void Enqueue(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A deleted user ID is required.", nameof(id));
        lock (_gate) { if (!File.Exists(PathFor(id))) Save(new(id)); }
    }
    public IReadOnlyList<CleanupJob> List()
    {
        lock (_gate)
        {
            return Directory.GetFiles(_folder, "*.json").Select(path => {
                var job = JsonSerializer.Deserialize<CleanupJob>(File.ReadAllText(path)) ?? throw new JsonException("Missing cleanup record.");
                if (job.UserId == Guid.Empty || Path.GetFileName(path) != job.UserId.ToString("N") + ".json") throw new JsonException("Invalid cleanup identity.");
                return job;
            }).ToArray();
        }
    }
    public void Save(CleanupJob job)
    {
        lock (_gate)
        {
            var path = PathFor(job.UserId); var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(job)); File.Move(temp, path, true);
        }
    }
    public void Complete(Guid id) { lock (_gate) File.Delete(PathFor(id)); }
}

public sealed class SeerrCleanupClient
{
    public async Task<string> Delete(PluginConfiguration config, Guid deletedUserId, CancellationToken token)
    {
        if (!Uri.TryCreate(config.SeerrApiUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || string.IsNullOrWhiteSpace(config.SeerrApiKey))
            throw new InvalidOperationException("Configure the Seerr URL and API key.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-Api-Key", config.SeerrApiKey);
        var root = config.SeerrApiUrl.TrimEnd('/') + "/api/v1/user/";
        using var lookup = await client.GetAsync(root + "jellyfin/" + deletedUserId.ToString("N"), token);
        if (lookup.StatusCode == HttpStatusCode.NotFound) return "absent";
        lookup.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(token));
        var user = data.RootElement;
        if (!user.TryGetProperty("jellyfinUserId", out var linked) || !Guid.TryParse(linked.GetString(), out var mediaId) || mediaId != deletedUserId)
            return "identity-review";
        if (!user.TryGetProperty("id", out var id) || !id.TryGetInt32(out var seerrId) || seerrId <= 1)
            return "protected-owner";
        using var deletion = await client.DeleteAsync(root + seerrId, token);
        if (deletion.StatusCode != HttpStatusCode.NotFound) deletion.EnsureSuccessStatusCode();
        return "deleted";
    }
}

public sealed class SeerrCleanupProcessor(SeerrCleanupStore store, SeerrCleanupClient client, ILogger<SeerrCleanupProcessor> logger)
{
    public async Task RunOnce(PluginConfiguration config, Func<Guid, bool> userStillExists, CancellationToken token = default)
    {
        if (!config.SeerrCleanupEnabled) return;
        IReadOnlyList<CleanupJob> jobs;
        try { jobs = store.List(); }
        catch (Exception ex) when (ex is IOException or JsonException) { logger.LogError("Seerr cleanup queue needs manual review; no deletions attempted."); return; }
        foreach (var job in jobs.Where(j => j.State == "pending" && (j.RetryAfter is null || j.RetryAfter <= DateTimeOffset.UtcNow)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (userStillExists(job.UserId)) { store.Save(job with { State = "live-user-review" }); continue; }
                var result = await client.Delete(config, job.UserId, token);
                if (result is "deleted" or "absent") { store.Complete(job.UserId); logger.LogInformation("Seerr account cleanup completed for deleted Jellyfin user {UserId}.", job.UserId); }
                else { store.Save(job with { State = result }); logger.LogWarning("Seerr cleanup needs review: {Reason}.", result); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                store.Save(job with { Attempts = job.Attempts + 1, RetryAfter = DateTimeOffset.UtcNow.AddSeconds(30 * Math.Pow(2, Math.Min(job.Attempts, 8))) });
                logger.LogWarning("Seerr cleanup could not complete; retained for retry.");
            }
        }
    }
}
