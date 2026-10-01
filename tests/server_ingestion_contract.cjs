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
const wireEvents = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/backend-events.json'), 'utf8'));
let locked = false;
let failFlush = false;
let flushes = 0;
let sheetOpens = 0;
let clock = Date.UTC(2026, 9, 1);
let lockAvailable = true;
const rateResponse = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/backend-rate-limited.json'), 'utf8'));
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
  Date: class extends Date {
    constructor(...args) { super(...(args.length ? args : [clock])); }
    static now() { return clock; }
  },
  Utilities: { newBlob: raw => ({ getBytes: () => Buffer.from(raw, 'utf8') }) },
  SpreadsheetApp: {
    openById: () => { sheetOpens++; return spreadsheet; },
    flush() {
      assert.equal(locked, true, 'flush must occur before releasing lock');
      if (failFlush) throw new Error('simulated_flush_failure');
      pending.splice(0).forEach(write => write());
      flushes++;
    }
  },
  LockService: { getScriptLock: () => ({
    tryLock() {
      assert.equal(locked, false);
      if (!lockAvailable) return false;
      locked = true;
      return true;
    },
    releaseLock() { assert.equal(locked, true); locked = false; }
  }) },
  CacheService: { getScriptCache: () => ({
    get: key => {
      const entry = cache.get(key);
      return entry && entry.expires > clock ? entry.value : null;
    },
    remove: key => cache.delete(key),
    put(key, value, seconds) {
      assert.equal(locked, true);
      assert.equal(pending.length, 0, 'dedupe marker was set before flushing writes');
      cache.set(key, { value, expires: clock + seconds * 1000 });
    }
  }) },
  ContentService: {
    MimeType: { JSON: 'application/json' },
    createTextOutput: body => ({ body, setMimeType() { return this; } })
  }
};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { timeout: 2000 });
for (const type of ['attempt', 'speed_test', 'leaderboard_entry', 'mailbox_poll',
  'internet_probe', 'portal_response', 'registration_event', 'error']) {
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
  sheetOpens = 0;
  lockAvailable = true;
  clock = Date.UTC(2026, 9, 1);
}
function readBoard(limit = 50) {
  sandbox.request = { parameter: { route: 'leaderboard', limit } };
  const response = vm.runInContext('doGet(request)', sandbox, { timeout: 2000 });
  assert.equal(locked, false);
  return JSON.parse(response.body);
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
assert.equal([...cache.keys()].filter(key => /^(event|batch):/.test(key)).length, 0,
  'failed flush poisoned retry dedupe');
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

// Each route is independently limited before any Sheets access. Retries are
// requests too; their existing event identities still prevent duplicate rows.
for (const [route, limit, payload] of [
  ['telemetry', 12, fixture],
  ['speedtest', 6, speed],
  ['leaderboard', 6, leader],
  ['leaderboardcontrol', 30, {
    schema: 4, operation: 'status', install_id: speed.install_id, request_id: 'req-status-fixture'
  }]
]) {
  reset();
  for (let i = 0; i < limit; i++) assert.equal(post(route, payload).ok, true, route);
  const opens = sheetOpens;
  assert.deepEqual(post(route, payload), rateResponse, route);
  assert.equal(sheetOpens, opens, 'throttled request touched Sheets: ' + route);
  clock += 1000;
  assert.equal(post(route, payload).retry_after_seconds, 59);
  clock += 59000;
  assert.equal(post(route, payload).ok, true, 'window did not recover: ' + route);
  checks++;
}

reset();
// The client's maximum flush is eight full batches, not eight individual events.
for (let batchIndex = 0; batchIndex < 8; batchIndex++) {
  const events = Array.from({ length: 64 }, (_, i) => ({
    ...fixture.events[0], event_id: `event-backlog-${batchIndex}-${i}`
  }));
  assert.equal(post('telemetry', { batch_id: `batch-backlog-${batchIndex}`, events }).accepted, 64);
}
assert.equal(tables.get('Attempts').rows.length, 512); checks++;

reset();
for (let i = 0; i < 300; i++) {
  const result = post('speedtest', speed);
  assert.equal(result.ok, i < 6);
}
assert.equal(tables.get('SpeedTests').rows.length, 1);
assert.equal(sheetOpens, 6);
// Deliberately not addressing new-ID creation; route quotas are independent too.
assert.equal(post('speedtest', { ...speed, install_id: 'install-another-fixture', event_id: 'event-another-fixture' }).ok, true);
assert.equal(post('telemetry', fixture).ok, true); checks++;

// A mixed-ID batch is checked atomically: a rejected ID must not consume the
// allowance of another ID. This also supports queues retained across ID resets.
reset();
for (let i = 0; i < 12; i++) assert.equal(post('telemetry', fixture).ok, true);
const otherAttempt = { ...fixture.events[0], install_id: 'install-other-batch-user', event_id: 'event-other-batch-user' };
assert.equal(post('telemetry', { events: [otherAttempt, speed] }).error, 'rate_limited');
assert.equal(cache.has('rate:telemetry:' + otherAttempt.install_id), false);
assert.equal(post('telemetry', { events: [otherAttempt] }).ok, true); checks++;

reset();
assert.equal(post('leaderboard', leader).ok, true);
assert.equal(readBoard(1).entries.length, 1);
const opensAfterCacheFill = sheetOpens;
lockAvailable = false;
assert.equal(readBoard().ok, true, 'cached GET should not wait for a busy writer');
lockAvailable = true;
for (let i = 0; i < 100; i++) assert.equal(readBoard(i % 2 ? 1 : 250).total, 1);
assert.equal(sheetOpens, opensAfterCacheFill, 'GET limit variants bypassed shared cache');
clock += 15000;
assert.equal(readBoard().total, 1);
assert.equal(sheetOpens, opensAfterCacheFill + 1, 'cache did not expire');
assert.equal(post('leaderboardcontrol', {
  schema: 4, operation: 'leave', install_id: speed.install_id, request_id: 'req-leave-cache-0001'
}).ok, true);
assert.equal(readBoard().total, 0, 'leave remained visible in cached leaderboard');
assert.equal(post('leaderboard', { ...leader, install_id: 'install-new-cache-user', event_id: 'event-new-cache-user' }).ok, true);
assert.equal(readBoard().total, 1, 'new publication did not invalidate the cache');
assert.equal(post('leaderboardcontrol', {
  schema: 4, operation: 'rename', install_id: 'install-new-cache-user', request_id: 'req-rename-cache-0001', nickname: 'New nick'
}).ok, true);
assert.equal(readBoard().entries[0].nickname, 'New nick', 'rename did not invalidate the cache'); checks++;

reset();
lockAvailable = false;
assert.equal(post('telemetry', fixture).retry_after_seconds, 2);
assert.equal(readBoard().error, 'rate_limited');
assert.equal(sheetOpens, 0, 'busy lock must not open Sheets'); checks++;

reset();
const telemetryEvents = [...fixture.events, ...wireEvents.filter(event => event.event_type !== 'leaderboard_entry')];
const wireResult = post('telemetry', { batch_id: 'batch-all-wire-types', events: telemetryEvents });
assert.equal(wireResult.ok, true, JSON.stringify(wireResult));
assert.equal(wireResult.accepted, 7);
assert.equal(post('leaderboard', wireEvents.find(event => event.event_type === 'leaderboard_entry')).ok, true);
for (const name of ['Attempts', 'SpeedTests', 'Leaderboard', 'MailboxPolls',
  'InternetProbes', 'PortalResponses', 'RegistrationEvents', 'Errors']) {
  assert.equal(tables.get(name).rows.length, 1, name + ' wire fixture was not stored');
}
checks++;

console.log(`private ingestion contract: PASS (${checks} scenarios; in-memory only)`);
