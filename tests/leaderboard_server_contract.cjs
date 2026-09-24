#!/usr/bin/env node
'use strict';
// Safe read-only contract test for the owner's *private* schema-v4 Apps Script.
// Usage: node tests/leaderboard_server_contract.cjs /path/to/private-script.js
// No live Sheets, deployment changes, setupSheets(), or HTTP calls are made.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const scriptPath = process.argv[2];
if (!scriptPath) {
  console.error('Usage: node tests/leaderboard_server_contract.cjs <private-server.js>');
  process.exit(2);
}

let headers;
let rows = [];
let rangeReads = 0;
const sheet = {
  getLastRow: () => rows.length + 1,
  getRange: (start, col, count, width) => {
    assert.equal(col, 1);
    assert.equal(width, headers.length);
    assert.equal(count, start === 1 ? 1 : rows.length);
    rangeReads++;
    return {
      getDisplayValues: () => [headers],
      getValues: () => rows.map(row => row.slice())
    };
  }
};
const sandbox = {
  SpreadsheetApp: {
    openById: () => ({ getSheetByName: name => name === 'Leaderboard' ? sheet : null })
  },
  ContentService: {
    MimeType: { JSON: 'application/json' },
    createTextOutput: body => ({ body, setMimeType() { return this; } })
  }
};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: 'private-server.js' });
headers = [...vm.runInContext('LEADERBOARD_HEADERS', sandbox)];
const cfg = vm.runInContext('CONFIG', sandbox);
assert.equal(cfg.schema, 4);
assert.deepEqual(Array.from(cfg.acceptedSchemas), [1, 2, 3, 4]);
assert.equal(cfg.maxBatchEvents, 64);
assert.equal(cfg.maxPayloadBytes, 65536);

function record({ id = 'install-aaaaaaaaaaaaaaaa', nick = 'WiFi King', down = 100,
  up = 20, ping = 15, jitter = 3, loss = 0, time = 100 } = {}) {
  const values = { schema: 4, install_id: id, nickname: nick,
    download_mbps: down, upload_mbps: up, latency_ms: ping,
    jitter_ms: jitter, packet_loss_pct: loss, received_at_epoch_ms: time };
  return headers.map(key => values[key] ?? '');
}
function read(limit = 100) {
  const data = vm.runInContext(`getPublicLeaderboard_({parameter:{limit:${limit}}})`, sandbox);
  const result = JSON.parse(data.body);
  assert.equal(result.ok, true);
  assert.equal(result.entries.length, Math.min(limit, result.total));
  assert.deepEqual(result.entries.map(row => row.rank),
    result.entries.map((_, i) => i + 1));
  const payload = JSON.stringify(result);
  for (const forbidden of ['install_id', 'installId', 'test_id', 'event_id', 'entry_id', 'batch_id',
    'received_at_epoch_ms', 'wifi_band', 'time_bucket']) {
    assert.ok(!payload.includes('"' + forbidden + '"'), `${forbidden} leaked`);
  }
  return result;
}

let checks = 0;
rows = [];
assert.equal(read().total, 0); checks++;
rows = [
  record({ down: 100, up: 70, time: 1 }),
  record({ down: 115, up: 80, time: 2 }),
  record({ down: 120, up: 100, ping: 12, jitter: 8, loss: 5, time: 3 }),
  record({ down: 118, up: 200, ping: 1, jitter: 1, time: 4 })
];
let result = read();
assert.equal(result.total, 1);
assert.deepEqual([result.entries[0].download_mbps, result.entries[0].upload_mbps,
  result.entries[0].latency_ms, result.entries[0].jitter_ms,
  result.entries[0].packet_loss_pct], [120, 100, 12, 8, 5]); checks++;

rows.push(record({ nick: 'Алекс', down: 110, time: 5 }));
result = read();
assert.equal(result.total, 2);
assert.deepEqual(result.entries.map(e => e.nickname), ['WiFi King', 'Алекс']); checks++;

rows.push(record({ id: 'install-bbbbbbbbbbbbbbbb', down: 108, time: 6 }));
result = read();
assert.equal(result.total, 3);
assert.equal(result.entries.filter(e => e.nickname === 'WiFi King').length, 2); checks++;

rows.push(record({ down: 125, up: 2, ping: 35, jitter: 17, time: 7 }));
result = read();
assert.equal(result.total, 3);
assert.deepEqual([result.entries[0].nickname, result.entries[0].download_mbps,
  result.entries[0].upload_mbps, result.entries[0].jitter_ms],
  ['WiFi King', 125, 2, 17]); checks++;

assert.equal(read(2).total, 3);
assert.equal(read(2).entries.length, 2); checks++;

rows = [record({ id: '', nick: 'Legacy', down: 30 }),
  record({ id: '', nick: 'Legacy', down: 40 }),
  record({ id: 'not valid', nick: 'Legacy', down: 50 }),
  record({ id: 'not valid', nick: 'Legacy', down: 60 })];
assert.equal(read().total, 4); checks++;

rows = [record({ nick: 'Bad\x1b[31m', down: 90 }),
  record({ id: 'install-bbbbbbbbbbbbbbbb', nick: '=HYPERLINK()', down: 80 }),
  record({ id: 'install-bbbbbbbbbbbbbbbb', nick: '=HYPERLINK()', down: -42 }),
  record({ id: 'install-cccccccccccccccc', nick: '\u200d', down: 10 })];
result = read();
assert.equal(result.total, 2);
assert.ok(result.entries.every(e => !/[\x00-\x1f\x7f]/.test(e.nickname)));
assert.ok(result.entries.every(e => e.download_mbps === null || e.download_mbps >= 0)); checks++;

const id = 'install-cccccccccccccccc';
rows = [record({ id, nick: 'Tie', down: 100, up: 20, ping: 15, time: 1 }),
  record({ id, nick: 'Tie', down: 100, up: 25, ping: 35, time: 2 }),
  record({ id, nick: 'Tie', down: 100, up: 25, ping: 5, jitter: 11, time: 3 }),
  record({ id, nick: 'Tie', down: 100, up: 25, ping: 5, jitter: 7, time: 4 })];
result = read();
assert.equal(result.total, 1);
assert.equal(result.entries[0].jitter_ms, 7); checks++;

// Normalization precedes grouping; two visual equivalents are one pair.
rows = [record({ id, nick: '  WiFi  King ', down: 70 }),
  record({ id, nick: 'WiFi King', down: 75 })];
result = read();
assert.equal(result.total, 1);
assert.equal(result.entries[0].download_mbps, 75); checks++;

console.log(`private leaderboard contract: PASS (${checks} scenarios; ${rangeReads} in-memory reads; zero writes)`);
