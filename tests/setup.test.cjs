const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const setup = path.resolve(__dirname, '../setup-claude.cjs');

test('setup is repeatable, preserves unrelated settings, and restores the original status line', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'usage-widget-setup-test-'));
  const home = path.join(root, 'home'), local = path.join(root, 'local');
  const settingsPath = path.join(home, '.claude', 'settings.json');
  const original = { model: 'test-model', permissions: { allow: ['Read'] }, statusLine: { type: 'command', command: 'echo original', padding: 2 } };
  fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
  fs.writeFileSync(settingsPath, JSON.stringify(original));
  const run = (args = []) => {
    const result = spawnSync(process.execPath, [setup, ...args], { env: { ...process.env, USERPROFILE: home, HOME: home, LOCALAPPDATA: local }, encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr);
    return JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
  };
  try {
    const installed = run();
    assert.deepEqual(installed.permissions, original.permissions);
    assert.equal(installed.model, original.model);
    assert.equal(installed.statusLine.padding, 2);
    assert.match(installed.statusLine.command, /claude-bridge/);
    assert.deepEqual(run(), installed);
    const config = JSON.parse(fs.readFileSync(path.join(local, 'AIUsageWidget', 'bridge-config.json'), 'utf8'));
    assert.deepEqual(config.previousStatusLine, original.statusLine);
    assert.deepEqual(run(['--remove']), original);
  } finally {
    assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep));
    fs.rmSync(root, { recursive: true });
  }
});
