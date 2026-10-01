#!/usr/bin/env node
'use strict';
// Offline only: execute the externally supplied private server in an isolated
// VM with fake Sheets/cache/locks. No Google credentials, network or deployment.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const scriptPath = process.argv[2];
if (!scriptPath) throw new Error('Usage: node tests/server_ingestion_contract.cjs <private-server-file>');
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/queued-speedtest.json'), 'utf8'));
let locked = false;
let failFlush = false;
let flushes = 0;
const tables = new Map();
const cache = new Map();
const pending = [];
const spreadsheet = {
  getSheetByName(name) {
    const table = tables.get(name);
    if (!table) return null;
    return {
      getLastRow: () => table.rows.length + 1,
      getRange(start, column, count, width) {
        assert.equal(column, 1);
        assert.equal(width, table.headers.length);
        return {
          getDisplayValues: () => [table.headers],
          getValues: () => table.rows.slice(start - 2, start - 2 + count),
          setValues(rows) {
            assert.equal(locked, true, 'write outside script lock');
            assert.equal(rows.length, count);
            pending.push(() => table.rows.splice(start - 2, count, ...rows.map(row => [...row])));
          }
        };
      }
    };
  }
};
const sandbox = {
  Utilities: { newBlob: raw => ({ getBytes: () => Buffer.from(raw, 'utf8') }) },
  SpreadsheetApp: {
    openById: () => spreadsheet,
    flush() {
      assert.equal(locked, true, 'flush must occur before releasing lock');
      if (failFlush) throw new Error('simulated_flush_failure');
      pending.splice(0).forEach(write => write());
      flushes++;
    }
  },
  LockService: { getScriptLock: () => ({
    waitLock() { assert.equal(locked, false); locked = true; },
    releaseLock() { assert.equal(locked, true); locked = false; }
  }) },
  CacheService: { getScriptCache: () => ({
    get: key => cache.get(key) ?? null,
    put(key, value) {
      assert.equal(locked, true);
      assert.equal(pending.length, 0, 'dedupe marker was set before flushing writes');
      cache.set(key, value);
    }
  }) },
  ContentService: {
    MimeType: { JSON: 'application/json' },
    createTextOutput: body => ({ body, setMimeType() { return this; } })
  }
};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { timeout: 2000 });
for (const type of ['attempt', 'speed_test', 'leaderboard_entry']) {
  const def = vm.runInContext(`getStorageDefinition_('${type}')`, sandbox);
  tables.set(def.sheetName, { headers: [...def.headers], rows: [] });
}
tables.set('LeaderboardActions', { headers: [...vm.runInContext('LEADERBOARD_ACTION_HEADERS', sandbox)], rows: [] });

function post(route, payload) {
  sandbox.request = { parameter: { route }, postData: { contents: JSON.stringify(payload) } };
  const response = vm.runInContext('doPost(request)', sandbox, { timeout: 2000 });
  assert.equal(locked, false, 'request leaked its script lock');
  return JSON.parse(response.body);
}
function reset() {
  tables.forEach(table => { table.rows.length = 0; });
  cache.clear();
  pending.length = 0;
  failFlush = false;
  flushes = 0;
}
let checks = 0;

// Full validation + routing + storage, not a mock that always returns ok:true.
let result = post('telemetry', fixture);
assert.equal(result.ok, true, JSON.stringify(result));
assert.equal(result.accepted, 2);
assert.equal(tables.get('Attempts').rows.length, 1);
assert.equal(tables.get('SpeedTests').rows.length, 1);
assert.equal(flushes, 1); checks++;

// A lost response and identical retry must not duplicate either part of the batch.
result = post('telemetry', fixture);
assert.equal(result.duplicate_batch, true);
assert.equal(tables.get('Attempts').rows.length, 1);
assert.equal(tables.get('SpeedTests').rows.length, 1); checks++;

reset();
const speed = fixture.events[1];
assert.equal(post('telemetry', { batch_id: 'batch-speed-only-0001', events: [speed] }).ok, true); checks++;

reset();
assert.equal(post('speedtest', speed).ok, true);
// Immediate request succeeded but its reply was lost, so the client queued it.
result = post('telemetry', fixture);
assert.equal(result.ok, true);
assert.equal(result.accepted, 1);
assert.equal(result.duplicate_events, 1);
assert.equal(tables.get('SpeedTests').rows.length, 1); checks++;

const leader = {
  event_type: 'leaderboard_entry', schema: 4, entry_id: 'entry-fixture-0001',
  install_id: speed.install_id, test_id: speed.test_id, app_version: speed.app_version,
  event_id: 'event-leader-fixture-0001', nickname: 'Fixture', download_mbps: 84.3,
  wifi_signal_bucket: 'unknown', wifi_band: 'unknown', time_bucket: 'unknown'
};
assert.equal(post('telemetry', { events: [fixture.events[0], leader] }).error, 'route_event_mismatch');
assert.equal(tables.get('Leaderboard').rows.length, 0);
assert.equal(post('speedtest', fixture).error, 'route_event_mismatch');
assert.equal(post('leaderboard', speed).error, 'route_event_mismatch'); checks++;

assert.equal(post('telemetry', { ...speed, schema: 3 }).error, 'invalid_schema');
assert.equal(post('telemetry', { ...speed, unexpected: 'not allowed' }).error, 'unknown_field');
assert.equal(post('telemetry', { ...speed, event_type: 'invented' }).error, 'invalid_event_type');
assert.equal(post('telemetry', { events: [] }).error, 'invalid_batch_size');
assert.equal(post('telemetry', { events: Array(65).fill(speed) }).error, 'invalid_batch_size');
assert.equal(post('telemetry', { padding: 'x'.repeat(65537) }).error, 'payload_too_large'); checks++;

reset();
failFlush = true;
assert.equal(post('telemetry', fixture).error, 'simulated_flush_failure');
assert.equal(cache.size, 0, 'failed flush poisoned retry dedupe');
// This fake fails before committing anything; no transaction guarantee is implied.
pending.length = 0;
failFlush = false;
assert.equal(post('telemetry', fixture).ok, true);
assert.equal(tables.get('SpeedTests').rows.length, 1); checks++;

reset();
assert.equal(post('leaderboard', leader).ok, true);
assert.equal(post('leaderboardcontrol', {
  schema: 4, operation: 'rename', install_id: speed.install_id,
  request_id: 'request-rename-fixture-0001', nickname: 'Renamed'
}).nickname, 'Renamed');
assert.equal(tables.get('LeaderboardActions').rows.length, 1);
assert.equal(flushes, 2, 'lifecycle action was not flushed before readback'); checks++;

console.log(`private ingestion contract: PASS (${checks} scenarios; in-memory only)`);
