// Local fixture for the real Windows service, not a replacement for production-server tests.
const fs = require('node:fs');
const https = require('node:https');
const path = require('node:path');
const root = process.argv[2];
const config = JSON.parse(fs.readFileSync(path.join(root, 'server.json'), 'utf8').replace(/^\uFEFF/, ''));
const server = https.createServer({pfx: fs.readFileSync(config.pfx), passphrase: config.password}, (req, res) => {
  if (req.url !== '/device/heartbeat' || req.method !== 'POST' || req.headers['x-club-device'] !== config.device) {
    res.writeHead(403).end('{}'); return;
  }
  let data = '';
  req.on('data', chunk => { data += chunk; if (data.length > 16384) req.destroy(); });
  req.on('end', () => {
    try {
      const report = JSON.parse(data);
      fs.writeFileSync(path.join(root, 'report.json'), JSON.stringify(report));
      const grant = JSON.parse(fs.readFileSync(path.join(root, 'grant.json'), 'utf8').replace(/^\uFEFF/, ''));
      res.writeHead(200, {'content-type': 'application/json'}).end(JSON.stringify(grant));
    } catch { res.writeHead(503).end('{}'); }
  });
});
server.listen(config.port || 0, '127.0.0.1', () => fs.writeFileSync(path.join(root, 'port.txt'), String(server.address().port)));
