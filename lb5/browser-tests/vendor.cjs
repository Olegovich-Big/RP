const fs = require('node:fs');
const path = require('node:path');
const source = path.join(__dirname, 'node_modules/@microsoft/signalr');
const target = path.join(__dirname, '../Valuator/wwwroot/lib/signalr');
fs.mkdirSync(target, { recursive: true });
for (const file of ['signalr.min.js', 'signalr.min.js.map']) {
    fs.copyFileSync(path.join(source, 'dist/browser', file), path.join(target, file));
}
// The npm package does not include LICENSE.txt. The upstream MIT license is bundled separately.
