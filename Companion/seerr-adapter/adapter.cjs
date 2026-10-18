/* A bounded HTML script injector; all API, media and asset traffic streams unchanged. */
const http = require('node:http');
const zlib = require('node:zlib');
const { pipeline } = require('node:stream');
const upstream = new URL(process.env.SEERR_UPSTREAM || 'http://127.0.0.1:5056');
if (upstream.protocol !== 'http:' || upstream.hostname !== '127.0.0.1') throw Error('Adapter upstream must be loopback HTTP');
const injection = '<script defer src="/jellyfin-handoff/config.js"></script><script defer src="/jellyfin-handoff/same-tab.js?v=1.2.1"></script>';
const server = http.createServer((request, response) => {
  const headers = { ...request.headers };
  if (headers.accept?.includes('text/html')) { headers['accept-encoding'] = 'identity'; delete headers['if-none-match']; delete headers['if-modified-since']; }
  const proxy = http.request({ hostname: upstream.hostname, port: upstream.port, path: request.url, method: request.method, headers, timeout: 30000 }, remote => {
    proxy.setTimeout(0);
    const replyHeaders = { ...remote.headers };
    const isHtml = request.method === 'GET' && remote.statusCode === 200 && /^text\/html\b/i.test(replyHeaders['content-type'] || '');
    if (!isHtml) { response.writeHead(remote.statusCode, replyHeaders); pipeline(remote, response, () => {}); return; }
    const encoding = replyHeaders['content-encoding'];
    let stream = remote;
    if (encoding === 'gzip') stream = remote.pipe(zlib.createGunzip());
    else if (encoding === 'br') stream = remote.pipe(zlib.createBrotliDecompress());
    else if (encoding === 'deflate') stream = remote.pipe(zlib.createInflate());
    else if (encoding && encoding !== 'identity') { response.writeHead(remote.statusCode, replyHeaders); pipeline(remote, response, () => {}); return; }
    delete replyHeaders['transfer-encoding']; delete replyHeaders['content-encoding']; delete replyHeaders['content-length']; delete replyHeaders.etag; delete replyHeaders['last-modified'];
    replyHeaders['cache-control'] = 'no-store';
    let size = 0, chunks = [], passthrough = false;
    stream.on('data', chunk => {
      if (passthrough) { response.write(chunk); return; }
      chunks.push(chunk); size += chunk.length;
      if (size > 4 * 1024 * 1024) { passthrough = true; response.writeHead(remote.statusCode, replyHeaders); for (const part of chunks) response.write(part); chunks = []; }
    });
    stream.on('end', () => {
      if (passthrough) { response.end(); return; }
      const html = Buffer.concat(chunks).toString('utf8');
      const end = html.toLowerCase().lastIndexOf('</body>');
      const body = Buffer.from(end < 0 ? html : html.slice(0, end) + injection + html.slice(end));
      replyHeaders['content-length'] = body.length;
      response.writeHead(remote.statusCode, replyHeaders); response.end(body);
    });
    stream.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end('Seerr temporarily unavailable'); });
    response.on('close', () => remote.destroy());
  });
  proxy.on('timeout', () => proxy.destroy());
  proxy.on('error', () => { if (!response.headersSent) response.writeHead(502, { 'Content-Type': 'text/plain' }); response.end('Seerr temporarily unavailable'); });
  request.on('aborted', () => proxy.destroy()); request.pipe(proxy);
});
server.listen(Number(process.env.PORT || 5055), process.env.LISTEN_HOST || '0.0.0.0');
