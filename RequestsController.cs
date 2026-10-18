using System.Reflection;
using System.Text.Encodings.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WizarrRequests;

[ApiController]
[Route("WizarrRequests")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RequestsController(RequestStore store, WizarrClient wizarr) : ControllerBase
{
    [AllowAnonymous, HttpGet("")]
    public IActionResult Page() => Asset("request.html", "text/html; charset=utf-8");
    [AllowAnonymous, HttpGet("request.js")]
    public IActionResult Script() => Asset("request.js", "application/javascript; charset=utf-8");
    [AllowAnonymous, HttpGet("style.css")]
    public IActionResult Style() => Asset("style.css", "text/css; charset=utf-8");
    [AllowAnonymous, HttpGet("info")]
    public object Info() => new { enabled = Plugin.Instance.Configuration.Enabled, serverName = Plugin.Instance.Configuration.ServerName };

    [AllowAnonymous, HttpPost("requests"), RequestSizeLimit(8192)]
    public IActionResult Submit([FromBody] ProfileInput input)
    {
        return Handle(() => {
            if (!Plugin.Instance.Configuration.Enabled) throw new RequestError(503, "The server is not accepting new requests right now.");
            CheckOrigin();
            var submitted = store.Submit(input, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", DateTimeOffset.UtcNow);
            // Requests only: never call Jellyfin user creation or Wizarr here.
            return new { id = submitted.Id, token = submitted.Token, status = "pending" };
        });
    }

    [AllowAnonymous, HttpGet("requests/{id}/status")]
    public IActionResult Status(string id) => Handle(() => {
        var token = Request.Headers["X-Request-Token"].ToString();
        var r = store.Status(id, token);
        return new { status = r.Status, created = r.Created, decided = r.Decided, invitationUrl = r.Status == "approved" ? r.InvitationUrl : null, invitationExpires = r.InvitationExpires };
    });

    [Authorize(Policy = Policies.RequiresElevation), HttpGet("admin/requests")]
    public IActionResult List() => Handle(() => store.List().Select(r => new {
        id = r.Id, name = r.Name, note = r.Note, status = r.Status,
        created = r.Created, decided = r.Decided, decidedBy = r.DecidedBy,
        invitationUrl = r.InvitationUrl, invitationExpires = r.InvitationExpires, reviewNote = r.ReviewNote
    }));

    [Authorize(Policy = Policies.RequiresElevation), HttpPost("admin/requests/{id}/approve")]
    public async Task<IActionResult> Approve(string id)
    {
        try
        {
            CheckOrigin();
            var config = Plugin.Instance.Configuration;
            WizarrClient.Validate(config);
            var request = store.BeginApproval(id, User.Identity?.Name ?? "Jellyfin administrator");
            try
            {
                // Do not use RequestAborted: the persisted decision must finish if the browser leaves.
                var invite = await wizarr.Create(config, CancellationToken.None);
                var invitationUrl = config.PrefillUsername ? invite.Url + "?username=" + Uri.EscapeDataString(request.Name) : invite.Url;
                store.CompleteApproval(id, invitationUrl, invite.Expires);
                return Ok(new { invitationUrl, invitationExpires = invite.Expires });
            }
            catch (Exception)
            {
                store.ApprovalUncertain(id);
                return StatusCode(502, new { error = "Approval needs review. Check Wizarr for an invitation before retrying; no account has been created by this plugin." });
            }
        }
        catch (RequestError e) { return StatusCode(e.Status, new { error = e.Message }); }
    }

    [Authorize(Policy = Policies.RequiresElevation), HttpPost("admin/requests/{id}/decline")]
    public IActionResult Decline(string id) => Handle(() => {
        CheckOrigin(); store.Deny(id, User.Identity?.Name ?? "Jellyfin administrator"); return new { status = "declined" };
    });

    private IActionResult Handle(Func<object> operation)
    {
        try { return Ok(operation()); }
        catch (RequestError e) { return StatusCode(e.Status, new { error = e.Message }); }
    }
    private void CheckOrigin()
    {
        if (Request.Headers["Sec-Fetch-Site"] == "cross-site") throw new RequestError(403, "Open this page directly to submit your request.");
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase))) throw new RequestError(403, "Cross-site submissions are not allowed.");
    }
    private IActionResult Asset(string name, string mime)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.WizarrRequests.Web." + name);
        if (stream == null) return NotFound();
        if (name == "request.html")
        {
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd().Replace("__ROOT__", HtmlEncoder.Default.Encode(Request.PathBase + "/WizarrRequests")), mime);
        }
        return File(stream, mime);
    }
}
