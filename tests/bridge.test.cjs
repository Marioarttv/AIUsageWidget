const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { normalize, writeSnapshot } = require('../scripts/claude-bridge.cjs');
const now = new Date('2026-09-30T12:00:00Z');
const reset = Math.floor(now.getTime() / 1000) + 7200;

test('preserves zero usage, percentage precision, and reset timestamps', () => {
  const result = normalize({ rate_limits: { five_hour: { used_percentage: 0, resets_at: reset }, seven_day: { used_percentage: 82.4, resets_at: reset + 10080 } } }, now);
  assert.equal(result.windows.length, 2);
  assert.equal(result.windows[0].usedPercent, 0);
  assert.equal(result.windows[0].resetsAt, reset);
  assert.equal(result.windows[1].usedPercent, 82.4);
});
test('does not mistake context-window usage for subscription usage', () => {
  assert.deepEqual(normalize({ context_window: { used_percentage: 99 } }, now).windows, []);
});
test('ignores missing, invalid, and expired subscription windows', () => {
  assert.deepEqual(normalize({ rate_limits: { five_hour: { used_percentage: null }, seven_day: { used_percentage: 50, resets_at: reset - 8000 } } }, now).windows, []);
});
test('an empty reading keeps the last valid snapshot', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'usage-widget-test-'));
  try {
    const valid = normalize({ rate_limits: { five_hour: { used_percentage: 23, resets_at: reset } } }, now);
    writeSnapshot(valid, directory);
    writeSnapshot(normalize({}, now), directory);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(directory, 'claude.json'), 'utf8')), valid);
    assert.equal(fs.readdirSync(directory).length, 1);
  } finally {
    assert.ok(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep));
    fs.rmSync(directory, { recursive: true });
  }
});
