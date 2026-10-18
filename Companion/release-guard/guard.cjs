'use strict';

// Optional qBittorrent bridge for Radarr/Sonarr. No upstream code is modified.
const http = require('node:http');
const fs = require('node:fs');
const { TextDecoder } = require('node:util');
const MAX_BODY = 8 * 1024 * 1024;
const HOME_SOURCE = /^(WEBDL|WEBRip|Bluray|DVD)(?:-|$)/i;
class Held extends Error {}

function torrentName(bytes) {
  let pos = 0, nodes = 0;
  function parse(depth = 0) {
    if (depth > 48 || ++nodes > 100000 || pos >= bytes.length) throw new Held('Invalid torrent metadata');
    const token = bytes[pos];
    if (token === 105) {
      const end = bytes.indexOf(101, ++pos);
      if (end < 0 || !/^-?\d+$/.test(bytes.subarray(pos, end).toString())) throw new Held('Invalid integer');
      pos = end + 1; return null;
    }
    if (token === 100 || token === 108) {
      pos++; const dict = token === 100; const result = dict ? Object.create(null) : [];
      while (bytes[pos] !== 101) {
        if (pos >= bytes.length) throw new Held('Incomplete torrent metadata');
        if (dict) {
          const key = parse(depth + 1);
          if (!Buffer.isBuffer(key)) throw new Held('Invalid dictionary key');
          const name = key.toString('utf8');
          if (Object.hasOwn(result, name)) throw new Held('Duplicate torrent key');
          result[name] = parse(depth + 1);
        } else result.push(parse(depth + 1));
      }
      pos++; return result;
    }
    const colon = bytes.indexOf(58, pos);
    if (colon < 0 || colon - pos > 9 || !/^\d+$/.test(bytes.subarray(pos, colon).toString())) throw new Held('Invalid byte string');
    const length = Number(bytes.subarray(pos, colon).toString()); pos = colon + 1;
    if (length > MAX_BODY || pos + length > bytes.length) throw new Held('Incomplete byte string');
    const result = bytes.subarray(pos, pos + length); pos += length; return result;
  }
  const root = parse();
  if (pos !== bytes.length || !root?.info || Array.isArray(root.info)) throw new Held('Missing torrent info');
  const name = root.info['name.utf-8'] || root.info.name;
  if (!Buffer.isBuffer(name)) throw new Held('Missing torrent name');
  try { return validTitle(new TextDecoder('utf-8', { fatal: true }).decode(name)); }
  catch { throw new Held('Invalid torrent name'); }
}
function validTitle(name) {
  if (typeof name !== 'string' || !name.trim() || name.length > 1024 || /[\x00-\x1f]/.test(name)) throw new Held('Missing or invalid release name');
  return name;
}
async function addTitles(body, contentType) {
  if (!/^(multipart\/form-data|application\/x-www-form-urlencoded)(?:;|$)/i.test(contentType || '')) throw new Held('Unsupported add request');
  let data;
  try { data = await new Request('http://release-guard.invalid/', { method: 'POST', headers: { 'content-type': contentType }, body }).formData(); }
  catch { throw new Held('Invalid add request'); }
  const names = [];
  for (const [key, value] of data) {
    if (key === 'urls') {
      if (typeof value !== 'string') throw new Held('Invalid torrent URL');
      for (const text of value.split(/\r?\n/).filter(x => x.trim())) {
        let u; try { u = new URL(text.trim()); } catch { throw new Held('Invalid magnet'); }
        // Never fetch arbitrary URLs or guess an identity from a remote filename.
        if (u.protocol !== 'magnet:' || !u.searchParams.has('xt') || u.searchParams.getAll('dn').length !== 1) throw new Held('Release identity unavailable; use a named magnet or torrent file');
        names.push(validTitle(u.searchParams.get('dn')));
      }
    } else if (key === 'torrents') {
      if (typeof value === 'string') throw new Held('Expected torrent file');
      names.push(torrentName(Buffer.from(await value.arrayBuffer())));
    } else if (typeof value !== 'string') throw new Held('Unexpected uploaded file');
  }
  if (!names.length || names.length > 32) throw new Held('Missing or excessive torrent identities');
  return names;
}
function timestamp(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}(?:T|$)/.test(value)) return NaN;
  return Date.parse(value.length === 10 ? value + 'T00:00:00Z' : value);
}
function verifyRelease(kind, parsed, metadata, now = Date.now()) {
  const quality = (kind === 'radarr' ? parsed?.parsedMovieInfo : parsed?.parsedEpisodeInfo)?.quality?.quality?.name;
  if (!HOME_SOURCE.test(quality || '')) throw new Held('Waiting for a web or disc release');
  if (kind === 'radarr') {
    if (!parsed.movie?.id || metadata.id !== parsed.movie.id) throw new Held('Movie identity unavailable');
    const dates = [metadata.digitalRelease, metadata.physicalRelease].map(timestamp).filter(Number.isFinite);
    if (!dates.length) throw new Held('Waiting for a confirmed digital or disc release date');
    if (Math.min(...dates) > now) throw new Held('Waiting for the digital or disc release date');
  } else {
    const info = parsed.parsedEpisodeInfo;
    if (!parsed.series?.id || !Array.isArray(parsed.episodes) || !parsed.episodes.length || !Array.isArray(metadata)) throw new Held('Episode identity unavailable');
    if (info.isMultiSeason || info.isSeasonExtra) throw new Held('Ambiguous multi-season or extra release; episode identity must be verified');
    let episodes = parsed.episodes.map(e => metadata.find(m => m.id === e.id && m.seriesId === parsed.series.id));
    for (const number of info.episodeNumbers || []) {
      if (!episodes.some(e => e?.seasonNumber === info.seasonNumber && e.episodeNumber === number)) throw new Held('An included episode has no known metadata');
    }
    for (const number of info.absoluteEpisodeNumbers || []) {
      if (!episodes.some(e => e?.absoluteEpisodeNumber === number)) throw new Held('An included episode has no known metadata');
    }
    if (info.fullSeason) {
      if (!Number.isInteger(info.seasonNumber)) throw new Held('Season identity unavailable');
      const season = metadata.filter(e => e.seasonNumber === info.seasonNumber && e.seriesId === parsed.series.id);
      if (!season.length) throw new Held('Season episodes unavailable');
      episodes = season;
    }
    if (episodes.some(e => !e || !Number.isFinite(timestamp(e.airDateUtc)) || timestamp(e.airDateUtc) > now)) throw new Held('Waiting for all included episodes to have known past air dates');
  }
}
async function jsonGet(base, key, path) {
  const r = await fetch(base.replace(/\/$/, '') + path, { headers: { 'X-Api-Key': key }, signal: AbortSignal.timeout(15000), redirect: 'error' });
  if (!r.ok) throw new Held('Release metadata service unavailable');
  return r.json();
}
async function checkTitle(kind, title, service, get = jsonGet) {
  const parsed = await get(service.url, service.apiKey, '/api/v3/parse?title=' + encodeURIComponent(title));
  const id = kind === 'radarr' ? parsed?.movie?.id : parsed?.series?.id;
  if (!Number.isSafeInteger(id) || id <= 0) throw new Held('Release identity could not be matched');
  const path = kind === 'radarr' ? '/api/v3/movie/' + id : '/api/v3/episode?seriesId=' + id;
  const metadata = await get(service.url, service.apiKey, path);
  verifyRelease(kind, parsed, metadata);
}
function cleanHeaders(headers) {
  const out = { ...headers };
  for (const name of ['connection','keep-alive','proxy-authenticate','proxy-authorization','te','trailer','transfer-encoding','upgrade','host']) delete out[name];
  return out;
}
async function readBody(req) {
  const parts = []; let length = 0;
  for await (const chunk of req) {
    length += chunk.length;
    if (length > MAX_BODY) throw new Held('Add request exceeds limit');
    parts.push(chunk);
  }
  return Buffer.concat(parts);
}
function createServer(kind, config, dependencies = {}) {
  const upstream = new URL(config.qbittorrentUrl);
  if (upstream.protocol !== 'http:' || upstream.pathname !== '/') throw new Error('Use a fixed HTTP qBittorrent origin');
  const request = dependencies.request || http.request;
  const check = dependencies.check || checkTitle;
  const server = http.createServer(async (req, res) => {
    if (req.method === 'GET' && req.url === '/health') { res.writeHead(200, { 'Content-Type':'application/json' }); res.end('{"ready":true}'); return; }
    const path = req.url?.split('?')[0];
    // Do not permit path normalization, alternate add paths, legacy APIs or redirects to bypass the check.
    if (!/^\/api\/v2\/[a-zA-Z]+\/[a-zA-Z]+$/.test(path || '')) { res.writeHead(403); res.end('Unsupported qBittorrent API route'); return; }
    let body;
    try {
      body = await readBody(req);
      if (path.toLowerCase() === '/api/v2/torrents/add') {
        if (req.method !== 'POST' || path !== '/api/v2/torrents/add') throw new Held('Unsupported torrent add route');
        const names = await addTitles(body, req.headers['content-type']);
        for (const title of names) await check(kind, title, config[kind]);
      }
    } catch (e) {
      res.writeHead(e instanceof Held ? 409 : 503, { 'Content-Type':'text/plain', 'Cache-Control':'no-store' });
      res.end(e instanceof Held ? 'Release held: ' + e.message : 'Release held: eligibility could not be verified');
      console.log(JSON.stringify({ service:kind, outcome:'held', reason:e instanceof Held ? e.message : 'verification unavailable' }));
      return;
    }
    const headers = cleanHeaders(req.headers); headers['content-length'] = body.length;
    const proxy = request({ hostname: upstream.hostname, port:upstream.port || 80, method:req.method, path:req.url, headers, timeout:30000 }, back => {
      res.writeHead(back.statusCode, cleanHeaders(back.headers)); back.pipe(res);
    });
    proxy.on('timeout', () => proxy.destroy(new Error('Upstream timeout')));
    proxy.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end('qBittorrent unavailable'); });
    req.on('aborted', () => proxy.destroy()); proxy.end(body);
  });
  server.requestTimeout = 60000; server.headersTimeout = 15000;
  return server;
}
if (require.main === module) {
  const config = JSON.parse(fs.readFileSync(process.env.RELEASE_GUARD_CONFIG || '/config/private.json', 'utf8'));
  for (const kind of ['radarr','sonarr']) {
    createServer(kind, config).listen(config[kind].port, config.listenHost, () => console.log(JSON.stringify({ service:kind, outcome:'listening' })));
  }
}
module.exports = { Held, torrentName, addTitles, verifyRelease, checkTitle, createServer };
