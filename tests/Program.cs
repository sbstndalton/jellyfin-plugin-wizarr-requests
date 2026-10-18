using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.WizarrRequests;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

var directory = Path.Combine(Path.GetTempPath(), "wizarr-requests-tests-" + Guid.NewGuid().ToString("N"));
var store = new RequestStore(directory);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<WizarrClient>();
builder.Services.AddControllers();
builder.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuth>("Synthetic", _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy(Policies.RequiresElevation, p => p.RequireAuthenticatedUser().RequireRole("admin")));
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
var creations = 0; var remoteFailure = false;
JsonElement lastPayload = default;
app.MapPost("/fake/api/invitations", async (HttpRequest request) => {
    if (request.Headers["X-API-Key"] != "synthetic-integration-key") return Results.Unauthorized();
    Interlocked.Increment(ref creations);
    using var json = await JsonDocument.ParseAsync(request.Body); lastPayload = json.RootElement.Clone();
    await Task.Delay(100);
    if (remoteFailure) return Results.StatusCode(500);
    return Results.Json(new { invitation = new { code = "SYNTH12345", expires = "2026-10-25T12:00:00Z" } }, statusCode: 201);
});
var cleanupMode = "normal"; var seerrDeletes = 0; var seerrLookups = 0; var deletedSeerrId = 0;
app.MapGet("/fake/api/v1/user/jellyfin/{id}", (string id, HttpRequest request) => {
    if (request.Headers["X-Api-Key"] != "synthetic-seerr-key") return Results.Unauthorized();
    seerrLookups++;
    if (cleanupMode == "absent") return Results.NotFound();
    if (cleanupMode == "failure") return Results.StatusCode(503);
    return Results.Json(new { id = cleanupMode == "owner" ? 1 : 42, jellyfinUserId = cleanupMode == "mismatch" ? Guid.NewGuid().ToString("N") : id });
});
app.MapDelete("/fake/api/v1/user/{id:int}", (int id) => { seerrDeletes++; deletedSeerrId = id; return Results.Ok(); });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Plugin.Instance.Configuration.WizarrApiUrl = address + "/fake";
if (args.Contains("--preview")) { Console.WriteLine("Synthetic UI preview: " + address + "/WizarrRequests"); await app.WaitForShutdownAsync(); return; }
using var client = new HttpClient { BaseAddress = new Uri(address) };
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); count++; }
async Task<HttpResponseMessage> Call(string path, HttpMethod method, string? role = null, object? body = null, string? token = null, string? origin = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (role != null) request.Headers.Add("X-Synthetic-Role", role);
    if (token != null) request.Headers.Add("X-Request-Token", token);
    if (origin != null) request.Headers.Add("Origin", origin);
    if (body != null) request.Content = JsonContent.Create(body);
    return await client.SendAsync(request);
}
async Task<JsonElement> Json(HttpResponseMessage response) => (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.Clone();
try
{
    var page = await Call("/WizarrRequests", HttpMethod.Get);
    Check(page.StatusCode == HttpStatusCode.OK && (await page.Content.ReadAsStringAsync()).Contains("novalidate"), "public wizard and per-step validation");
    Check(page.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"), "public wizard CSP");
    var input = new ProfileInput("Synthetic Applicant", "Synthetic test only");
    Check((await Call("/WizarrRequests/requests", HttpMethod.Post, body: input, origin: "https://untrusted.example.invalid")).StatusCode == HttpStatusCode.Forbidden, "cross-origin request rejected");
    var submit = await Call("/WizarrRequests/requests", HttpMethod.Post, body: input);
    var submitted = await Json(submit); var id = submitted.GetProperty("id").GetString()!; var token = submitted.GetProperty("token").GetString()!;
    Check(submit.StatusCode == HttpStatusCode.OK && creations == 0 && store.List().Single().Status == "pending", "profile submission creates no invitation or account");
    Check((await Call($"/WizarrRequests/requests/{id}/status", HttpMethod.Get, token: new string('0',64))).StatusCode == HttpStatusCode.NotFound, "invalid status token rejected");
    var status = await Json(await Call($"/WizarrRequests/requests/{id}/status", HttpMethod.Get, token: token));
    Check(status.GetProperty("status").GetString() == "pending" && !status.TryGetProperty("email", out _) && status.GetProperty("invitationUrl").ValueKind == JsonValueKind.Null, "private status reveals no applicant data or pending invite");
    Check((await Call("/WizarrRequests/admin/requests", HttpMethod.Get)).StatusCode == HttpStatusCode.Unauthorized, "anonymous queue access rejected");
    Check((await Call("/WizarrRequests/admin/requests", HttpMethod.Get, "user")).StatusCode == HttpStatusCode.Forbidden, "regular user queue access rejected");
    Check((await Call($"/WizarrRequests/admin/requests/{id}/approve", HttpMethod.Post)).StatusCode == HttpStatusCode.Unauthorized, "anonymous approval rejected");
    Check((await Call($"/WizarrRequests/admin/requests/{id}/approve", HttpMethod.Post, "user")).StatusCode == HttpStatusCode.Forbidden, "regular user approval rejected");
    var approvals = await Task.WhenAll(Call($"/WizarrRequests/admin/requests/{id}/approve", HttpMethod.Post, "admin"), Call($"/WizarrRequests/admin/requests/{id}/approve", HttpMethod.Post, "admin"));
    Check(approvals.Count(x => x.StatusCode == HttpStatusCode.OK) == 1 && approvals.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1 && creations == 1, "concurrent approval creates exactly one invitation");
    Check(!lastPayload.GetProperty("unlimited").GetBoolean() && lastPayload.GetProperty("duration").GetString() == "unlimited" && lastPayload.GetProperty("expires_in_days").GetInt32() == 7, "single-use invite and seven-day expiry; no user expiry");
    Check(!lastPayload.GetProperty("allow_downloads").GetBoolean() && !lastPayload.GetProperty("allow_live_tv").GetBoolean() && !lastPayload.GetProperty("allow_mobile_uploads").GetBoolean(), "restricted invitation permissions");
    Check(lastPayload.GetProperty("server_ids")[0].GetInt32() == 7 && lastPayload.GetProperty("library_ids").GetArrayLength() == 2, "explicit configured server and libraries");
    status = await Json(await Call($"/WizarrRequests/requests/{id}/status", HttpMethod.Get, token: token));
    Check(status.GetProperty("invitationUrl").GetString() == "https://invites.example.invalid/j/SYNTH12345", "approved applicant receives full Wizarr link");
    var persisted = new RequestStore(directory);
    Check(persisted.Status(id,token).Status == "approved" && !File.ReadAllText(Path.Combine(directory,"requests.json")).Contains(token), "approval persists with hashed status tokens");
    var denied = store.Submit(input with { Name="Declined applicant" },"synthetic-two",DateTimeOffset.UtcNow);
    Check((await Call($"/WizarrRequests/admin/requests/{denied.Id}/decline",HttpMethod.Post,"admin")).StatusCode == HttpStatusCode.OK && creations == 1, "decline creates no invitation");
    remoteFailure = true;
    var uncertain = store.Submit(input with { Name="Uncertain applicant" },"synthetic-three",DateTimeOffset.UtcNow);
    Check((await Call($"/WizarrRequests/admin/requests/{uncertain.Id}/approve",HttpMethod.Post,"admin")).StatusCode == HttpStatusCode.BadGateway && store.Status(uncertain.Id,uncertain.Token).Status == "review-required", "uncertain remote response requires manual review");
    Check((await Call($"/WizarrRequests/admin/requests/{uncertain.Id}/approve",HttpMethod.Post,"admin")).StatusCode == HttpStatusCode.Conflict && creations == 2, "uncertain approval is never blindly retried");
    Plugin.Instance.Configuration.PrefillUsername=true;
    Plugin.Instance.Configuration.LibraryIds=[];
    var unconfigured=store.Submit(input with {Name="Unconfigured applicant"},"synthetic-four",DateTimeOffset.UtcNow);
    Check((await Call($"/WizarrRequests/admin/requests/{unconfigured.Id}/approve",HttpMethod.Post,"admin")).StatusCode == HttpStatusCode.ServiceUnavailable && store.Status(unconfigured.Id,unconfigured.Token).Status=="pending", "missing library selection fails before approval");
    Check((await Call("/WizarrRequests/requests",HttpMethod.Post,body:input)).StatusCode == HttpStatusCode.TooManyRequests,"duplicate request throttled");
    Check((await Call("/WizarrRequests/requests",HttpMethod.Post,body:input with {Name=""})).StatusCode == HttpStatusCode.BadRequest,"invalid profile rejected");
    var interrupted=store.Submit(input with {Name="Interrupted applicant"},"synthetic-five",DateTimeOffset.UtcNow);
    store.BeginApproval(interrupted.Id,"synthetic admin");
    Check(new RequestStore(directory).Status(interrupted.Id,interrupted.Token).Status=="review-required","restart during approval requires reconciliation, never another invitation");
    Plugin.Instance.Configuration.Enabled=false;
    Check((await Call("/WizarrRequests/requests",HttpMethod.Post,body:input)).StatusCode == HttpStatusCode.ServiceUnavailable,"owner can close requests");
    Plugin.Instance.Configuration.Enabled=true; Plugin.Instance.Configuration.LibraryIds=[4,5]; remoteFailure=false;
    var prefilled=store.Submit(input with {Name="Prefill Test"},"synthetic-six",DateTimeOffset.UtcNow);
    var prefilledResult=await Json(await Call($"/WizarrRequests/admin/requests/{prefilled.Id}/approve",HttpMethod.Post,"admin"));
    Check(prefilledResult.GetProperty("invitationUrl").GetString()!.EndsWith("?username=Prefill%20Test"),"opt-in name prefill is URL encoded");
    File.WriteAllText(Path.Combine(directory,"requests.json"),"broken json");
    try { _=new RequestStore(directory); throw new Exception("Queue corruption silently accepted"); } catch(JsonException){Check(true,"corrupt queue fails closed");}
    var cleanup = new SeerrCleanupStore(directory);
    var processor = new SeerrCleanupProcessor(cleanup, new SeerrCleanupClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SeerrCleanupProcessor>.Instance);
    var config = Plugin.Instance.Configuration; config.SeerrApiUrl = address + "/fake"; config.SeerrApiKey = "synthetic-seerr-key";
    var removed = Guid.NewGuid(); cleanup.Enqueue(removed); cleanup.Enqueue(removed);
    Check(new SeerrCleanupStore(directory).List().Count == 1, "deleted-user queue persists and deduplicates events");
    await processor.RunOnce(config, _ => false);
    Check(seerrLookups == 0 && cleanup.List().Count == 1, "Seerr cleanup disabled by default");
    config.SeerrCleanupEnabled = true;
    await processor.RunOnce(config, _ => false);
    Check(seerrDeletes == 1 && deletedSeerrId == 42 && cleanup.List().Count == 0, "deleted media identity removes exactly its matching Seerr account");
    cleanupMode = "absent"; cleanup.Enqueue(Guid.NewGuid()); await processor.RunOnce(config, _ => false);
    Check(seerrDeletes == 1 && cleanup.List().Count == 0, "missing Seerr account is an idempotent cleanup success");
    cleanupMode = "owner"; var owner = Guid.NewGuid(); cleanup.Enqueue(owner); await processor.RunOnce(config, _ => false);
    Check(seerrDeletes == 1 && cleanup.List().Single().State == "protected-owner", "Seerr owner account is never deleted");
    cleanupMode = "mismatch"; var mismatch = Guid.NewGuid(); cleanup.Enqueue(mismatch); await processor.RunOnce(config, _ => false);
    Check(seerrDeletes == 1 && cleanup.List().Single(j => j.UserId == mismatch).State == "identity-review", "mismatched media identity never triggers deletion");
    cleanupMode = "normal"; var live = Guid.NewGuid(); cleanup.Enqueue(live); var calls = seerrLookups; await processor.RunOnce(config, _ => true);
    Check(seerrLookups == calls && cleanup.List().Single(j => j.UserId == live).State == "live-user-review", "restored Jellyfin user blocks queued deletion");
    cleanupMode = "failure"; var retry = Guid.NewGuid(); cleanup.Enqueue(retry); await processor.RunOnce(config, _ => false);
    var pending = cleanup.List().Single(j => j.UserId == retry);
    Check(pending.Attempts == 1 && pending.RetryAfter > DateTimeOffset.UtcNow && seerrDeletes == 1, "Seerr outage retains cleanup for retry");
    calls = seerrLookups; await processor.RunOnce(config, _ => false);
    Check(seerrLookups == calls, "cleanup retries respect backoff");
    cleanup.Save(pending with { RetryAfter = null }); cleanupMode = "normal"; await processor.RunOnce(config, _ => false);
    Check(seerrDeletes == 2 && cleanup.List().All(j => j.UserId != retry), "queued cleanup recovers after Seerr returns");
    File.WriteAllText(Directory.GetFiles(Path.Combine(directory,"seerr-cleanup"),"*.json").First(), "corrupt");
    calls = seerrLookups; await processor.RunOnce(config, _ => false);
    Check(seerrLookups == calls, "corrupt cleanup queue fails closed before any remote deletion");
    Console.WriteLine($"{count} checks passed; all data and remote invitations synthetic.");
}
finally { await app.StopAsync(); await app.DisposeAsync(); Directory.Delete(directory,true); }

public sealed class SyntheticAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role=Request.Headers["X-Synthetic-Role"].ToString();
        if(role.Length==0)return Task.FromResult(AuthenticateResult.NoResult());
        var claims=new[]{new Claim(ClaimTypes.Name,"Synthetic administrator"),new Claim(ClaimTypes.Role,role)};
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
    }
}
