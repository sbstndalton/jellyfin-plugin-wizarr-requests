using System.Net.Http.Json;
using System.Text.Json;

namespace Jellyfin.Plugin.WizarrRequests;

public sealed record InviteResult(string Url, DateTimeOffset? Expires);
public sealed class WizarrClient
{
    public static void Validate(PluginConfiguration config)
    {
        if (!Uri.TryCreate(config.WizarrApiUrl, UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query)) throw new RequestError(503, "Configure a valid Wizarr API address first.");
        if (!Uri.TryCreate(config.WizarrPublicUrl, UriKind.Absolute, out var external) || external.Scheme != "https" || !string.IsNullOrEmpty(external.UserInfo) || !string.IsNullOrEmpty(external.Query) || !string.IsNullOrEmpty(external.Fragment)) throw new RequestError(503, "Configure a public HTTPS Wizarr address first.");
        if (string.IsNullOrWhiteSpace(config.WizarrApiKey) || config.ServerId < 1 || config.LibraryIds.Length == 0 || config.LibraryIds.Any(x => x < 1)) throw new RequestError(503, "Configure the Wizarr key, server and explicit allowed libraries first.");
    }

    public async Task<InviteResult> Create(PluginConfiguration config, CancellationToken cancellationToken)
    {
        Validate(config);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("X-API-Key", config.WizarrApiKey);
        // In Wizarr, unlimited controls INVITE REUSE, not account lifetime. False is essential.
        using var response = await client.PostAsJsonAsync(config.WizarrApiUrl.TrimEnd('/') + "/api/invitations", new {
            server_ids = new[] { config.ServerId }, library_ids = config.LibraryIds, expires_in_days = 7,
            unlimited = false, duration = "unlimited", allow_downloads = false, allow_live_tv = false, allow_mobile_uploads = false
        }, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Created) throw new HttpRequestException("Wizarr did not confirm invitation creation.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var invitation = document.RootElement.GetProperty("invitation");
        var code = invitation.GetProperty("code").GetString() ?? "";
        if (code.Length is < 6 or > 64 || code.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new HttpRequestException("Wizarr returned an invalid invitation code.");
        DateTimeOffset? expires = null;
        if (invitation.TryGetProperty("expires", out var expiry) && DateTimeOffset.TryParse(expiry.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) expires = parsed;
        return new(config.WizarrPublicUrl.TrimEnd('/') + "/j/" + Uri.EscapeDataString(code), expires);
    }
}
