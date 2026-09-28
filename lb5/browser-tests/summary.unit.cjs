const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const code = fs.readFileSync(path.join(__dirname, '../Valuator/wwwroot/js/summary.js'), 'utf8');

test('late pending snapshot cannot hide a delivered rank; unrelated events are ignored', async () => {
    const id = '00000000-0000-0000-0000-000000000001';
    const elements = Object.fromEntries(['evaluation', 'connection-status', 'similarity', 'rank', 'worker', 'pending', 'result']
        .map(name => [name, { dataset: {}, textContent: '', hidden: false }]));
    elements.evaluation.dataset = { textId: id, completed: 'false' };
    let callback, resolveSnapshot;
    const connection = {
        state: 'Disconnected',
        start: async () => {},
        invoke: () => new Promise(resolve => { resolveSnapshot = resolve; }),
        on: (_, handler) => { callback = handler; },
        onreconnecting() {}, onreconnected() {}, onclose() {}, stop: async () => {}
    };
    class Builder {
        withUrl() { return this; }
        withAutomaticReconnect() { return this; }
        build() { return connection; }
    }
    vm.runInNewContext(code, {
        document: { getElementById: name => elements[name] },
        window: { addEventListener() {} },
        signalR: { HubConnectionBuilder: Builder, HttpTransportType: { WebSockets: 1 }, HubConnectionState: { Disconnected: 'Disconnected' } },
        setTimeout, clearTimeout
    });
    await new Promise(resolve => setImmediate(resolve));
    callback({ textId: id, rank: 0.5, similarity: 0, worker: 'worker1' });
    resolveSnapshot({ textId: id, rank: null, similarity: 0 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(elements.rank.textContent, 0.5);
    assert.equal(elements.pending.hidden, true);
    assert.equal(elements.result.hidden, false);
    callback({ textId: 'other', rank: 1, similarity: 1 });
    assert.equal(elements.rank.textContent, 0.5);
    assert.equal(elements.similarity.textContent, 0);
});
