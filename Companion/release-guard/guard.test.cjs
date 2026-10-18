'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { once } = require('node:events');
const { Held, torrentName, addTitles, verifyRelease, checkTitle, createServer } = require('./guard.cjs');
const NOW = Date.parse('2026-10-18T12:00:00Z');
const movie = { movie:{ id:1 }, parsedMovieInfo:{ quality:{ quality:{ name:'WEBDL-1080p' } } } };
const tv = { series:{ id:2 }, episodes:[{ id:3 }], parsedEpisodeInfo:{ seasonNumber:1, quality:{ quality:{ name:'WEBRip-1080p' } } } };
const episode = { id:3, seriesId:2, seasonNumber:1, airDateUtc:'2026-10-17T20:00:00Z' };
test('movie requires actual digital/disc date, never cinema+90day fallback', () => {
  verifyRelease('radarr', movie, { id:1, digitalRelease:'2026-10-18' }, NOW);
  verifyRelease('radarr', movie, { id:1, physicalRelease:'2026-10-17', digitalRelease:'2027-01-01' }, NOW);
  for (const metadata of [{ id:1, digitalRelease:'2026-10-19' },{ id:1, inCinemas:'2020-01-01' },{ id:1 },{ id:1,digitalRelease:'invalid' }]) assert.throws(() => verifyRelease('radarr', movie, metadata, NOW), Held);
});
test('unknown, broadcast, cam and screener sources fail closed', () => {
  for (const name of ['SDTV','HDTV-1080p','CAM','Telesync','DVDSCR','Unknown','']) {
    const p = structuredClone(movie); p.parsedMovieInfo.quality.quality.name = name;
    assert.throws(() => verifyRelease('radarr', p, { id:1,digitalRelease:'2000-01-01' }, NOW), Held);
  }
  for (const name of ['DVD','WEBDL-480p','WEBRip-2160p','Bluray-1080p Remux']) {
    const p = structuredClone(movie); p.parsedMovieInfo.quality.quality.name = name;
    verifyRelease('radarr', p, { id:1,physicalRelease:'2000-01-01' }, NOW);
  }
});
test('identity mismatch and unmapped metadata fail closed', async () => {
  assert.throws(() => verifyRelease('radarr', movie, { id:4,digitalRelease:'2000-01-01' }, NOW), Held);
  await assert.rejects(checkTitle('radarr','Unknown',{url:'http://unused',apiKey:'synthetic'},async()=>({})),Held);
});
test('TV needs known past dates for every included episode', () => {
  verifyRelease('sonarr', tv, [episode], NOW);
  for (const episodes of [[],[{...episode,airDateUtc:undefined}],[{...episode,airDateUtc:'2027-01-01T00:00:00Z'}]]) assert.throws(() => verifyRelease('sonarr', tv, episodes, NOW),Held);
  const multi = structuredClone(tv); multi.episodes.push({id:4});
  assert.throws(()=>verifyRelease('sonarr',multi,[episode],NOW),Held);
});
test('season packs cannot pass because just their first episode aired', () => {
  const season=structuredClone(tv); season.parsedEpisodeInfo.fullSeason=true;
  assert.throws(()=>verifyRelease('sonarr',season,[episode,{...episode,id:4,airDateUtc:'2027-01-01T00:00:00Z'}],NOW),Held);
  verifyRelease('sonarr',season,[episode,{...episode,id:4}],NOW);
});
test('partial number matches and ambiguous multi-season packs are held', () => {
  const p=structuredClone(tv);p.parsedEpisodeInfo.episodeNumbers=[1,2];
  assert.throws(()=>verifyRelease('sonarr',p,[{...episode,episodeNumber:1}],NOW),Held);
  p.parsedEpisodeInfo.episodeNumbers=[];p.parsedEpisodeInfo.isMultiSeason=true;
  assert.throws(()=>verifyRelease('sonarr',p,[episode],NOW),Held);
});
function torrent(name) { const n=Buffer.from(name);return Buffer.concat([Buffer.from('d4:infod4:name'+n.length+':'),n,Buffer.from('ee')]); }
test('reads actual torrent info name and rejects malformed/duplicate metadata', () => {
  assert.equal(torrentName(torrent('Film.2026.1080p.WEB-DL')), 'Film.2026.1080p.WEB-DL');
  for (const b of [Buffer.from('bad'),Buffer.from('d4:infod4:name1:a4:name1:bee'),Buffer.concat([torrent('x'),Buffer.from('extra')])]) assert.throws(()=>torrentName(b),Held);
});
test('named magnets and uploaded torrents parse; remote URLs and unidentified magnets do not',async()=>{
  const type='application/x-www-form-urlencoded';
  const magnet='magnet:?xt=urn:btih:synthetic&dn=Film.2026.1080p.WEB-DL';
  assert.deepEqual(await addTitles(Buffer.from(new URLSearchParams({urls:magnet}).toString()),type),['Film.2026.1080p.WEB-DL']);
  for(const urls of ['https://indexer.invalid/1.torrent','magnet:?xt=urn:btih:synthetic','magnet:?xt=x&dn=a&dn=b']) await assert.rejects(addTitles(Buffer.from(new URLSearchParams({urls}).toString()),type),Held);
  const form=new FormData();form.set('torrents',new Blob([torrent('Film.2026.1080p.WEB-DL')]),'opaque.torrent');
  const r=new Request('http://unused',{method:'POST',body:form});
  assert.deepEqual(await addTitles(Buffer.from(await r.arrayBuffer()),r.headers.get('content-type')),['Film.2026.1080p.WEB-DL']);
});
test('HTTP boundary holds ineligible adds, passes eligible adds unchanged, preserves native auth',async(t)=>{
  let adds=0, auth;
  const upstream=http.createServer(async(req,res)=>{let b='';for await(const c of req)b+=c;if(req.url==='/api/v2/torrents/add'){adds++;auth=req.headers.cookie;res.end(b);}else {res.setHeader('set-cookie','SID=synthetic; Path=/; HttpOnly');res.end('Ok.');}});
  upstream.listen(0,'127.0.0.1');await once(upstream,'listening');t.after(()=>upstream.close());
  const guard=createServer('radarr',{qbittorrentUrl:'http://127.0.0.1:'+upstream.address().port+'/',radarr:{}},{check:async(_kind,title)=>{if(title==='Future')throw new Held('Waiting for release');}});
  guard.listen(0,'127.0.0.1');await once(guard,'listening');t.after(()=>guard.close());const base='http://127.0.0.1:'+guard.address().port;
  function add(title){return fetch(base+'/api/v2/torrents/add',{method:'POST',headers:{Cookie:'SID=synthetic'},body:new URLSearchParams({urls:'magnet:?xt=urn:btih:x&dn='+title})});}
  assert.equal((await add('Future')).status,409);assert.equal(adds,0);
  const allowed=await add('Released');assert.equal(allowed.status,200);assert.equal(await allowed.text(),new URLSearchParams({urls:'magnet:?xt=urn:btih:x&dn=Released'}).toString());assert.equal(adds,1);assert.equal(auth,'SID=synthetic');
  const login=await fetch(base+'/api/v2/auth/login',{method:'POST',body:new URLSearchParams({username:'synthetic',password:'synthetic'})});assert.match(login.headers.get('set-cookie'),/SID=synthetic/);
  assert.equal((await fetch(base+'/api/v2/torrents/%61dd',{method:'POST'})).status,403);assert.equal(adds,1);
  assert.equal((await fetch(base+'/api/v2/torrents/Add',{method:'POST'})).status,409);assert.equal(adds,1);
});
test('metadata outage returns held response without contacting download client',async(t)=>{
  const guard=createServer('sonarr',{qbittorrentUrl:'http://127.0.0.1:1/',sonarr:{}},{check:async()=>{throw new Error('synthetic outage');},request:()=>{assert.fail('Must not forward');}});
  guard.listen(0,'127.0.0.1');await once(guard,'listening');t.after(()=>guard.close());
  const r=await fetch('http://127.0.0.1:'+guard.address().port+'/api/v2/torrents/add',{method:'POST',body:new URLSearchParams({urls:'magnet:?xt=x&dn=Future'})});assert.equal(r.status,503);
});
