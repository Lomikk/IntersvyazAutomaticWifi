#!/usr/bin/env node
'use strict';
// Safe in-memory contract test for the owner's private schema-v4 Apps Script.
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

let leaderboardHeaders;
let actionHeaders;
let leaderboardRows = [];
let actionRows = [];
let rangeReads = 0;

function makeSheet(kind) {
  const getHeaders = () => kind === 'leaderboard' ? leaderboardHeaders : actionHeaders;
  const getRows = () => kind === 'leaderboard' ? leaderboardRows : actionRows;
  return {
    getLastRow: () => getRows().length + 1,
    getRange: (start, col, count, width) => {
      assert.equal(col, 1);
      assert.equal(width, getHeaders().length);
      rangeReads++;
      return {
        getDisplayValues: () => [getHeaders()],
        getValues: () => getRows().slice(start - 2, start - 2 + count).map(row => row.slice()),
        setValues: values => {
          assert.equal(values.length, count);
          const rows = getRows();
          for (let i = 0; i < values.length; i++) {
            const target = start - 2 + i;
            if (target === rows.length) rows.push(values[i].slice());
            else rows[target] = values[i].slice();
          }
          return this;
        }
      };
    }
  };
}

const leaderboardSheet = makeSheet('leaderboard');
const actionSheet = makeSheet('actions');
const spreadsheet = {
  getSheetByName: name => name === 'Leaderboard' ? leaderboardSheet :
    name === 'LeaderboardActions' ? actionSheet : null
};
const sandbox = {
  SpreadsheetApp: { openById: () => spreadsheet, flush() {} },
  LockService: { getScriptLock: () => ({ tryLock() { return true; }, releaseLock() {} }) },
  // This suite mutates rows directly to exercise projections/lifecycle rules.
  // Cache and real rate windows are exercised by server_ingestion_contract.cjs.
  CacheService: { getScriptCache: () => ({ get: () => null, put() {}, remove() {} }) },
  ContentService: {
    MimeType: { JSON: 'application/json' },
    createTextOutput: body => ({ body, setMimeType() { return this; } })
  }
};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: 'private-server.js' });
leaderboardHeaders = [...vm.runInContext('LEADERBOARD_HEADERS', sandbox)];
actionHeaders = [...vm.runInContext('LEADERBOARD_ACTION_HEADERS', sandbox)];
const cfg = vm.runInContext('CONFIG', sandbox);
assert.equal(cfg.schema, 4);
assert.deepEqual(Array.from(cfg.acceptedSchemas), [4]);
assert.equal(cfg.maxBatchEvents, 64);
assert.equal(cfg.maxPayloadBytes, 65536);
assert.equal(cfg.leaderboardNicknameMaxLength, 18);
assert.equal(cfg.leaderboardRenameLimit, 3);
assert.equal(vm.runInContext('typeof migrateLeaderboardLifecycleV4', sandbox), 'undefined');
assert.equal(vm.runInContext('typeof migrateSchema4', sandbox), 'undefined');
assert.equal(vm.runInContext('typeof setupSheets', sandbox), 'undefined');
assert.equal(vm.runInContext('typeof sendSafeSpeedTestSelfTest', sandbox), 'undefined');

function record({ id = 'install-aaaaaaaaaaaaaaaa', nick = 'WiFi King', down = 100,
  up = 20, ping = 15, jitter = 3, loss = 0, time = 100 } = {}) {
  const values = { schema: 4, install_id: id, nickname: nick,
    download_mbps: down, upload_mbps: up, latency_ms: ping,
    jitter_ms: jitter, packet_loss_pct: loss, received_at_epoch_ms: time };
  return leaderboardHeaders.map(key => values[key] ?? '');
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
function control(operation, { id = 'install-aaaaaaaaaaaaaaaa', nick, request = `req-${operation}-12345678` } = {}) {
  const payload = { schema: 4, operation, install_id: id, request_id: request };
  if (nick !== undefined) payload.nickname = nick;
  try {
    const output = vm.runInContext(`handleLeaderboardControl_(${JSON.stringify(payload)})`, sandbox);
    return JSON.parse(output.body);
  } catch (err) {
    return { ok: false, error: String(err && err.message ? err.message : err) };
  }
}
function validatePublish({ id = 'install-aaaaaaaaaaaaaaaa', nick = 'WiFi King' } = {}) {
  try {
    vm.runInContext(`validateLeaderboardPublicationUnderLock_(SpreadsheetApp.openById('x'), ${JSON.stringify({ install_id: id, nickname: nick })})`, sandbox);
    return null;
  } catch (err) {
    return String(err && err.message ? err.message : err);
  }
}

let checks = 0;
leaderboardRows = [];
actionRows = [];
assert.equal(read().total, 0); checks++;

leaderboardRows = [
  record({ down: 100, up: 70, time: 1 }),
  record({ down: 115, up: 80, time: 2 }),
  record({ down: 120, up: 100, ping: 12, jitter: 8, loss: 5, time: 3 }),
  record({ nick: 'Алекс', down: 110, time: 5 })
];
let result = read();
assert.equal(result.total, 1);
assert.equal(result.entries[0].nickname, 'Алекс');
assert.deepEqual([result.entries[0].download_mbps, result.entries[0].upload_mbps,
  result.entries[0].latency_ms, result.entries[0].jitter_ms,
  result.entries[0].packet_loss_pct], [120, 100, 12, 8, 5]); checks++;

leaderboardRows.push(record({ id: 'install-bbbbbbbbbbbbbbbb', nick: 'Алекс', down: 108, time: 6 }));
result = read();
assert.equal(result.total, 2);
assert.equal(result.entries.filter(e => e.nickname === 'Алекс').length, 2); checks++;

// A successful rename changes the one public position without changing its best measurement.
let renamed = control('rename', { nick: 'WiFi King', request: 'req-rename-00000001' });
assert.equal(renamed.ok, true);
assert.equal(renamed.nickname, 'WiFi King');
result = read();
assert.equal(result.total, 2);
assert.equal(result.entries[0].nickname, 'WiFi King');
assert.equal(result.entries[0].download_mbps, 120); checks++;

// Repeating exactly the same request is idempotent and does not consume another rename.
const actionCountAfterRename = actionRows.length;
renamed = control('rename', { nick: 'WiFi King', request: 'req-rename-00000001' });
assert.equal(renamed.ok, true);
assert.equal(actionRows.length, actionCountAfterRename); checks++;

// Three successful changes in the rolling window are allowed; a fourth is rejected.
assert.equal(control('rename', { nick: 'King 2', request: 'req-rename-00000002' }).ok, true);
assert.equal(control('rename', { nick: 'King 3', request: 'req-rename-00000003' }).ok, true);
const limitedRename = control('rename', { nick: 'King 4', request: 'req-rename-00000004' });
assert.deepEqual(limitedRename, { ok: false, error: 'rename_rate_limited' }); checks++;

// Leaving is immediate and blocks both the public view and delayed publications.
const left = control('leave', { request: 'req-leave-00000001' });
assert.equal(left.ok, true);
assert.equal(left.active, false);
assert.equal(read().entries.some(e => e.download_mbps === 120), false);
assert.equal(validatePublish({ nick: 'King 3' }), 'leaderboard_inactive'); checks++;

// First rejoin is allowed and restores the historical best record; another rejoin inside 24h is blocked.
const joined = control('join', { nick: 'Return King', request: 'req-join-00000001' });
assert.equal(joined.ok, true);
assert.equal(joined.active, true);
result = read();
assert.equal(result.entries[0].nickname, 'Return King');
assert.equal(result.entries[0].download_mbps, 120);
assert.equal(validatePublish({ nick: 'Return King' }), null);
assert.equal(validatePublish({ nick: 'stale nick' }), 'nickname_mismatch');
assert.equal(control('leave', { request: 'req-leave-00000002' }).ok, true);
const limitedJoin = control('join', { nick: 'Again', request: 'req-join-00000002' });
assert.deepEqual(limitedJoin, { ok: false, error: 'rejoin_rate_limited' }); checks++;

// Exit is always available even after the rename limit has been exhausted.
const secondId = 'install-cccccccccccccccc';
leaderboardRows.push(record({ id: secondId, nick: 'Tie', down: 100, up: 20, ping: 15, time: 1 }));
assert.equal(control('rename', { id: secondId, nick: 'Tie2', request: 'req-c-ren-00000001' }).ok, true);
assert.equal(control('rename', { id: secondId, nick: 'Tie3', request: 'req-c-ren-00000002' }).ok, true);
assert.equal(control('rename', { id: secondId, nick: 'Tie4', request: 'req-c-ren-00000003' }).ok, true);
assert.equal(control('leave', { id: secondId, request: 'req-c-leave-000001' }).ok, true); checks++;

// Public nicknames are capped at 18 scalars and technical identifiers never escape.
leaderboardRows = [record({ id: 'install-dddddddddddddddd', nick: 'Я'.repeat(80), down: 90 })];
actionRows = [];
result = read();
assert.equal(result.entries[0].nickname.length, 18);
assert.ok(!JSON.stringify(result).includes('install-dddddddddddddddd')); checks++;

// Legacy rows without a valid install_id remain independent rather than being guessed together.
leaderboardRows = [record({ id: '', nick: 'Legacy', down: 30 }),
  record({ id: '', nick: 'Legacy', down: 40 }),
  record({ id: 'not valid', nick: 'Legacy', down: 50 })];
actionRows = [];
assert.equal(read().total, 3); checks++;

assert.equal(read(2).entries.length, 2); checks++;

// Receipt time, not winning speed or physical row order, selects a legacy nickname.
leaderboardRows = [record({ nick: 'Newest', down: 30, time: 50 }),
  record({ nick: 'Older', down: 40, time: 10 })];
actionRows = [];
assert.equal(read().entries[0].nickname, 'Newest');
assert.equal(read().entries[0].download_mbps, 40); checks++;

// An explicit lifecycle nickname remains authoritative over measurement history.
assert.equal(control('rename', { nick: 'Explicit', request: 'req-explicit-12345678' }).ok, true);
leaderboardRows.push(record({ nick: 'Stale upload', time: Date.now() + 1000 }));
assert.equal(read().entries[0].nickname, 'Explicit'); checks++;

console.log(`private leaderboard contract: PASS (${checks} scenarios; ${rangeReads} in-memory reads; ${actionRows.length} action rows in final scenario)`);
