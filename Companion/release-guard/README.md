# Optional release guard

A Node 22 HTTP bridge between Radarr/Sonarr and qBittorrent. This is a separate companion service; it does not modify Jellyfin, Seerr, Radarr, Sonarr or qBittorrent source.

Before forwarding a torrent-add request, it reads the torrent's actual info name or a magnet display name, resolves it through the appropriate Arr `/parse` API, and checks fresh library metadata:

- Movies require a known digital or physical release date that has arrived. A cinema date alone never qualifies.
- Every TV episode included must have a known air date in the past. Season packs are checked against the entire known season.
- Only WEB-DL, WEBRip, DVD or Blu-ray quality classifications qualify. Broadcast, cinema, screener and unknown sources are held.
- Missing identities, unsupported add formats, missing dates and metadata outages fail closed. Remote torrent URLs are not fetched; upload a torrent file or use a named magnet.

Other modern qBittorrent API calls are proxied, retaining its native login cookies. Bind to a private interface reachable by the Arr containers; do not publish this as a public endpoint. Point each Arr download client at its corresponding listener. Keep its existing qBittorrent credentials. The bridge supports fixed HTTP upstream origins and no legacy API routes.

Configuration is a private JSON file, selected by `RELEASE_GUARD_CONFIG` (default `/config/private.json`):

```json
{
  "listenHost": "127.0.0.1",
  "qbittorrentUrl": "http://127.0.0.1:8080/",
  "radarr": { "port": 5057, "url": "http://127.0.0.1:7878", "apiKey": "YOUR_KEY" },
  "sonarr": { "port": 5058, "url": "http://127.0.0.1:8989", "apiKey": "YOUR_KEY" }
}
```

Run `node guard.cjs`; run synthetic tests with `node --test guard.test.cjs`. Keep native minimum availability set to Released, availability delay zero or later, and home-source quality profiles as defense in depth. Requests remain monitored while held and can download when an eligible release is subsequently found. This is not a scheduler that repeatedly searches indexers.

The check relies on Arr metadata and release-name classification. Incorrect upstream dates or falsely labelled files cannot be independently authenticated by this bridge. Existing torrents and direct/manual qBittorrent additions do not pass through it. If adding another download client or replacing Arr settings, route it through the guard too.
