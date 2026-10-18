# Wizarr Requests for Jellyfin

A Jellyfin plugin for **requesting access before receiving a Wizarr invitation**.

Visitors fill out a short profile wizard. A Jellyfin administrator reviews the request and approves or declines it. Only approval calls Wizarr’s API to create a personal, single-use invitation.

**Early release. Tested with Jellyfin 12.2.0 and Wizarr 2026.10.1.** Other versions and third-party Jellyfin clients have not been qualified. Please back up your server configuration before installing.

## The flow

1. On the Jellyfin browser login page, choose **Request access**.
2. Enter your name and a short note.
3. Submit and save the private status link.
4. The administrator reviews the profile in **Dashboard → Plugins → Wizarr Requests**.
5. Approval creates a **single-use Wizarr invitation that expires in seven days**.
6. The applicant returns to their private status link, opens the invitation, and chooses their password in Wizarr.

Submitting a request creates no Jellyfin account. Declining one creates no invitation. The applicant confirms their username during Wizarr setup. Optional name prefill carries the requested name into Wizarr when that installation supports it.

## Install

Requirements: Jellyfin **12.2.0**, Wizarr with its `/api/invitations` API, and a Wizarr API key. The server must be able to reach Wizarr’s API, and guests must be able to open its public HTTPS invitation links. An owner-only reverse-proxy login in front of Wizarr will block guests.

### Jellyfin plugin catalog

Add this third-party repository under **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/sbstndalton/jellyfin-plugin-wizarr-requests/main/manifest.json
```

Then install **Wizarr Requests** from the catalog, restart when playback is idle, and follow configuration steps 4–6 below. This repository currently distributes the **preview release**.

### Manual installation

1. Download the plugin ZIP from [Releases](https://github.com/sbstndalton/jellyfin-plugin-wizarr-requests/releases).
2. Extract it into a new directory beneath Jellyfin’s `plugins` directory, for example `Wizarr Requests_1.2.1.0`. The directory should directly contain `Jellyfin.Plugin.WizarrRequests.dll` and `meta.json`.
3. Restart Jellyfin when nobody is playing media.
4. Open **Dashboard → Plugins → Wizarr Requests** and expand **Connection settings**.
5. Set your display name, internal Wizarr API URL, public HTTPS Wizarr URL, Wizarr API key, server ID and explicit comma-separated Wizarr library IDs. Wizarr’s authenticated `GET /api/servers` and `GET /api/libraries` endpoints provide those IDs.
6. Enable **Accept new requests**, then save.

Requests are disabled by default. The plugin never silently chooses all libraries. Downloads, live TV and uploads are disabled on invitations it creates. No administrator privilege is granted by the plugin.

### Wizarr account setup

Configure the public Jellyfin address in Wizarr’s media server settings. The plugin selects that configured server; applicants do not enter its address.

The optional **Prefill username from requested name** setting appends a URL-encoded `username` query parameter to new invitation links. Enable it only when your Wizarr installation supports that parameter. Applicants can edit the resulting username. Standard Wizarr versions may ignore it.

The companion [sbstndalton/Wizarr community fork](https://github.com/sbstndalton/wizarr) supports this parameter. Its [fork guide](https://github.com/sbstndalton/wizarr/blob/main/FORK.md) documents the signup changes, installation, and merging future upstream updates.

Email and password rules on the account creation screen belong to Wizarr. This plugin does not change them; custom Wizarr policy changes must be maintained separately.

### Add the login link

In Jellyfin’s **Branding → Login disclaimer**, append:

```html
<p>New here? <a href="/WizarrRequests">Request access</a><br>
<small>Accounts require the server owner’s approval.</small></p>
```

For a Jellyfin installation under a URL prefix, include that prefix in the link. If your theme hides login disclaimers, adjust the theme’s disclaimer visibility. The plugin itself does not overwrite branding or existing login text.

The browser wizard is available at `/WizarrRequests`. Native TV/mobile clients have their own login interfaces and do not automatically gain a registration button.

## Review requests

Use the plugin’s configuration page while signed in as a Jellyfin administrator. It shows the submitted profile and explicit **Approve and create invitation** / **Decline** actions. Applicants can retrieve approved invitations through the secret private status link. You can also copy the full invitation from the queue and share it yourself.

Confirm the person’s identity before approving. No email address is requested. There is no email delivery or phone push notification in this release, so review the queue directly. A bookmark to the plugin configuration page is useful on a phone.

### Helmarr

Approved invitations are normal Wizarr invitations and can be managed through Helmarr’s Wizarr integration. **This plugin does not add its pending-request queue or approval buttons to Helmarr.** No supported extension/API for adding that interface was verified. It does not send fake movie requests or repurpose Seerr notifications.

### Delete the linked Seerr account

Version 1.1 adds optional cleanup when a Jellyfin profile is deleted, including deletion through Jellyfin's own dashboard. In the plugin's connection settings, enable **Delete the linked Seerr account**, then enter your Seerr/Jellyseerr API URL and API key. This option is off by default and works independently of accepting new access requests.

The plugin records Jellyfin's user-deleted event and resolves the Seerr account by the exact Jellyfin user UUID. It never matches accounts by name or email. Cleanup runs in the background, normally within a few seconds. **Seerr's native account deletion also deletes that account's requests.** The Seerr owner (ID 1), mismatched identities, and Jellyfin accounts restored before cleanup are protected and require manual review.

If Seerr is unavailable, cleanup remains in the plugin data folder's `seerr-cleanup` directory and retries with backoff, including after a Jellyfin restart. Review jobs with a state other than `pending` manually; logs record completion, retry, and protection events without API keys. A corrupt queue fails closed. Disabling cleanup pauses queued jobs; enabling it does not sweep or delete existing Seerr users. Back up this directory along with configuration. No accounts are deleted merely to test the connection.

## Privacy and failure handling

- The request queue stays in Jellyfin’s plugin data folder. It contains the applicant’s name, note, request time and review result. Older records retain their previously submitted fields.
- Applicants’ passwords are never collected or stored by this plugin.
- Status secrets are random 256-bit tokens. Only their hashes are stored. The private status URL puts the secret in its fragment, not a query parameter.
- Public status responses do not return the submitted name or note. The admin queue and all decisions require Jellyfin’s administrator authorization policy.
- Submission throttling limits an address to five requests per day and the whole server to 100 per hour. An identical name and note from the same address is limited for 24 hours. A hidden spam field and cross-origin checks are included. This is basic abuse protection, not a CAPTCHA or comprehensive anti-spam service.
- If Wizarr’s response is uncertain, or Jellyfin restarts during approval, the request becomes **review-required**. The plugin does not blindly issue a second invitation. Inspect Wizarr and share any successfully created invitation manually.
- Requests are retained until the administrator removes the plugin data. No automatic retention cleanup or account revocation is implemented. Back up this directory along with Jellyfin’s configuration.

Keep Jellyfin’s configuration and backups private: the Wizarr API key is stored in the standard plugin configuration XML. Avoid publishing that XML or the request queue.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```sh
dotnet build -c Release
dotnet run --project tests/IntegrationTests.csproj -c Release
```

The executable test harness uses loopback HTTP, synthetic profiles, mock admin/user identities and a mock Wizarr API. It verifies submission-only behavior, authorization boundaries, private status tokens, concurrency, invitation flags, throttling, persistence and uncertain-result handling. It never contacts a real media server.

Build output: `bin/Release/net10.0/Jellyfin.Plugin.WizarrRequests.dll`. Package that DLL alongside `meta.json`; do not copy Jellyfin’s assemblies into the plugin folder.

The source follows Jellyfin’s plugin/controller model and Wizarr’s invitation API. It is a community project, not an official Jellyfin, Wizarr or Helmarr integration.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

### Optional Jellyfin → Seerr link sign-in

Version 1.2 adds a browser handoff using **native Jellyfin Quick Connect and native Seerr authentication**. It does not fork either server, send a Jellyfin bearer token to Seerr, put credentials in a link, or share cookies across subdomains.

Install the files in `Companion/seerr-handoff/` as `/jellyfin-handoff/` on the **same origin as Seerr**. In a Seerr Docker installation, mount that directory read-only at `/app/public/jellyfin-handoff` and restart Seerr so Next.js discovers the public files. Set `config.js` to the exact public Jellyfin origin, including HTTPS and any non-default port but no trailing slash or path. Persist the mount in your container manager. The upstream Seerr image remains unchanged.

Enable Quick Connect in Jellyfin, install the compatible File Transformation plugin, then enable **Seerr link sign-in** and enter the public HTTPS Seerr URL in this plugin's settings. Restart Jellyfin after installing the plugin update; later setting changes take effect after a web refresh. The feature defaults off.

An ordinary external Seerr link signs in through a temporary hidden frame on the Seerr origin, then opens the destination in the current tab. Jellyfin authorizes native Quick Connect as the current profile, and Seerr creates its regular session. Explicit Ctrl/Cmd/Shift-click remains normal browser behavior. The exchange checks the exact site origin, tab, random nonce and unchanged Jellyfin identity. Native Seerr permissions and imported-user policies still apply. Internal Enhanced detail modals and already-handled links retain their existing behavior. Do not install another site's copy of the companion configuration.

The background handoff requires compatible frame/cookie policies; the deployment verified here uses sibling subdomains on the same site. Different sites or restrictive third-party-cookie/frame policies can prevent it. A separate native client or independently hosted web shell may not load the plugin scripts. On failure, the source page offers ordinary Seerr navigation. This is a small plugin-and-static-page integration, not a promise of compatibility with every future server release.

Validation includes 43 .NET integration checks, 12 browser-protocol checks and 4 HTTP adapter checks, isolated native plugin/script loading and Seerr static-page hosting, and a desktop browser handoff through the native Quick Connect endpoint.

### Seerr → Jellyfin in the current tab

`Companion/seerr-handoff/same-tab.js` intercepts ordinary links to the exact Jellyfin origin configured in `config.js`. It leaves other destinations and explicit modifier-clicks alone. Load `config.js` then `same-tab.js` on Seerr pages through your reverse proxy, or use the optional small Node adapter in `Companion/seerr-adapter/adapter.cjs`. The adapter inserts those two static scripts into HTML; API and asset responses stream through unchanged. It requires no Seerr source changes or custom image.

For the provided adapter's default host-network setup, bind the upstream Seerr container's port 5055 to **127.0.0.1:5056**, and run the adapter on host port 5055. Mount its source read-only and run it with Node 22 or later. `SEERR_UPSTREAM` must be a loopback HTTP address; `PORT` defaults to 5055. Persist both the backend port binding and adapter service in your container manager. Keep the public Seerr URL pointed at the adapter. Remove the adapter and restore the original Seerr port binding to undo it.

When updating the companion, deploy the versioned HTML and JavaScript together so cached older scripts cannot break the handoff. No Jellyfin access token or Quick Connect secret is transported in a URL by the current frame-based implementation. The normal HTTP adapter does not implement WebSocket upgrade forwarding; reconsider it if a future Seerr release needs that transport.

### Optional download release check

The separate [release guard](Companion/release-guard/README.md) checks Radarr/Sonarr torrent additions before forwarding them to qBittorrent. Movies need a confirmed past digital/disc release date, TV episodes need known past air dates, and both need a web/disc source classification. Unknown dates and metadata outages hold the download. Existing requests can remain monitored while waiting. It uses native APIs and does not require an upstream fork. See its README for installation, limitations, and synthetic tests.
