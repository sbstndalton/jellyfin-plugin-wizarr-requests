# Wizarr Requests for Jellyfin

A Jellyfin plugin for **requesting access before receiving a Wizarr invitation**.

Visitors fill out a short profile wizard. A Jellyfin administrator reviews the request and approves or declines it. Only approval calls Wizarr’s API to create a personal, single-use invitation.

**Early release. Tested with Jellyfin 12.2.0 and Wizarr 2026.10.1.** Other versions and third-party Jellyfin clients have not been qualified. Please back up your server configuration before installing.

## The flow

1. On the Jellyfin browser login page, choose **Request access**.
2. Enter a name, email, desired username and a short note.
3. Submit and save the private status link.
4. The administrator reviews the profile in **Dashboard → Plugins → Wizarr Requests**.
5. Approval creates a **single-use Wizarr invitation that expires in seven days**.
6. The applicant returns to their private status link, opens the invitation, and chooses their password in Wizarr.

Submitting a request creates no Jellyfin account. Declining one creates no invitation. The desired username is a preference; the applicant confirms it during Wizarr setup.

## Install

Requirements: Jellyfin **12.2.0**, Wizarr with its `/api/invitations` API, and a Wizarr API key. The server must be able to reach Wizarr’s API, and guests must be able to open its public HTTPS invitation links. An owner-only reverse-proxy login in front of Wizarr will block guests.

1. Download the plugin ZIP from [Releases](https://github.com/sbstndalton/jellyfin-plugin-wizarr-requests/releases).
2. Extract it into a new directory beneath Jellyfin’s `plugins` directory, for example `Wizarr Requests_1.0.0.0`. The directory should directly contain `Jellyfin.Plugin.WizarrRequests.dll` and `meta.json`.
3. Restart Jellyfin when nobody is playing media.
4. Open **Dashboard → Plugins → Wizarr Requests** and expand **Connection settings**.
5. Set your display name, internal Wizarr API URL, public HTTPS Wizarr URL, Wizarr API key, server ID and explicit comma-separated Wizarr library IDs. Wizarr’s authenticated `GET /api/servers` and `GET /api/libraries` endpoints provide those IDs.
6. Enable **Accept new requests**, then save.

Requests are disabled by default. The plugin never silently chooses all libraries. Downloads, live TV and uploads are disabled on invitations it creates. No administrator privilege is granted by the plugin.

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

Email addresses are self-reported, not verified. Confirm the person’s identity before approving. There is no email delivery or phone push notification in this release, so review the queue directly. A bookmark to the plugin configuration page is useful on a phone.

### Helmarr

Approved invitations are normal Wizarr invitations and can be managed through Helmarr’s Wizarr integration. **This plugin does not add its pending-request queue or approval buttons to Helmarr.** No supported extension/API for adding that interface was verified. It does not send fake movie requests or repurpose Seerr notifications.

## Privacy and failure handling

- The request queue stays in Jellyfin’s plugin data folder. It contains the applicant’s name, email, desired username, note, request time and review result.
- Applicants’ passwords are never collected or stored by this plugin.
- Status secrets are random 256-bit tokens. Only their hashes are stored. The private status URL puts the secret in its fragment, not a query parameter.
- Public status responses do not return the submitted name, email or note. The admin queue and all decisions require Jellyfin’s administrator authorization policy.
- Submission throttling limits an address to five requests per day and the whole server to 100 per hour. Duplicate email submissions are limited for 24 hours. A hidden spam field and cross-origin checks are included. This is basic abuse protection, not a CAPTCHA or comprehensive anti-spam service.
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
