#!/usr/bin/env node
'use strict';

// Offline contract for the private Apps Script update route. The server source
// is supplied externally; this test never contacts Google, GitHub or Sheets.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const scriptPath = process.argv[2];
if (!scriptPath) throw new Error('Usage: node tests/update_manifest_server_contract.cjs <private-server-file>');

const hash = '39c2b82d4c94e65e18bdf14030cfc75c9a015ddc28a35e10db154ce605c8896f';
let remoteManifest = JSON.stringify({
  schema: 1,
  stable: null,
  prerelease: { version: 'v0.1.0-alpha.26', sha256: hash }
});
let fetches = 0;
let clock = Date.UTC(2026, 9, 2, 18, 0, 0);
const cache = new Map();

const sandbox = {
  Date: class extends Date {
    constructor(...args) { super(...(args.length ? args : [clock])); }
    static now() { return clock; }
  },
  ContentService: {
    MimeType: { JSON: 'application/json' },
    createTextOutput: body => ({ body, setMimeType() { return this; } })
  },
  CacheService: { getScriptCache: () => ({
    get: key => {
      const item = cache.get(key);
      return item && item.expires > clock ? item.value : null;
    },
    put: (key, value, seconds) => cache.set(key, { value, expires: clock + seconds * 1000 }),
    remove: key => cache.delete(key)
  }) },
  UrlFetchApp: {
    fetch(url, options) {
      fetches++;
      assert.equal(url, 'https://raw.githubusercontent.com/Lomikk/IntersvyazAutomaticWifi/main/update-manifest.json');
      assert.equal(options.muteHttpExceptions, true);
      return {
        getResponseCode: () => 200,
        getContentText: () => remoteManifest
      };
    }
  },
  SpreadsheetApp: {
    openById() { throw new Error('update route must not touch Sheets'); }
  }
};

vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { timeout: 2000 });

function get(channel) {
  sandbox.request = { parameter: { route: 'update', channel } };
  const response = vm.runInContext('doGet(request)', sandbox, { timeout: 2000 });
  return JSON.parse(response.body);
}

let result = get('stable');
assert.deepEqual(result, { channel: 'stable', available: false });
assert.equal(fetches, 1);

result = get('prerelease');
assert.equal(result.channel, 'prerelease');
assert.equal(result.available, true);
assert.equal(result.version, 'v0.1.0-alpha.26');
assert.equal(result.sha256, hash);
assert.equal(
  result.url,
  'https://github.com/Lomikk/IntersvyazAutomaticWifi/releases/download/v0.1.0-alpha.26/IS74Wifi-v0.1.0-alpha.26-win-x64.exe'
);
assert.equal(fetches, 1, 'second request bypassed update-manifest cache');

assert.equal(get('beta').error, 'invalid_update_channel');

clock += 301000;
remoteManifest = JSON.stringify({
  schema: 1,
  stable: { version: 'v0.1.0', sha256: 'x'.repeat(64) },
  prerelease: { version: 'v0.1.0', sha256: 'x'.repeat(64) }
});
result = get('stable');
assert.equal(result.ok, false);
assert.equal(result.error, 'invalid_update_manifest');
assert.equal(fetches, 2, 'expired cache did not re-fetch manifest');

console.log('PASS update-manifest-server');
