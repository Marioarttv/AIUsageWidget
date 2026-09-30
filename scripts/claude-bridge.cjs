'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');
const dataDir = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AIUsageWidget');

function normalize(input, now = new Date()) {
  const windows = [];
  for (const [key, label, minutes] of [['five_hour', '5-hour session', 300], ['seven_day', 'Weekly', 10080]]) {
    const value = input.rate_limits?.[key];
    if (!value || typeof value.used_percentage !== 'number' || !Number.isFinite(value.used_percentage)) continue;
    const reset = Number.isSafeInteger(value.resets_at) && value.resets_at > 0 && value.resets_at < 253402300800 ? value.resets_at : null;
    // Missing/expired windows are unknown, never interpreted as a full allowance.
    if (reset !== null && reset <= Math.floor(now.getTime() / 1000)) continue;
    windows.push({ key, label, usedPercent: Math.min(100, Math.max(0, value.used_percentage)), resetsAt: reset, windowMinutes: minutes });
  }
  return { updatedAt: now.toISOString(), source: 'Claude Code status line', windows };
}

function writeSnapshot(snapshot, directory = dataDir) {
  if (!snapshot.windows.length) return; // Keep last valid reading through startup/compaction.
  fs.mkdirSync(directory, { recursive: true });
  const destination = path.join(directory, 'claude.json');
  const temporary = `${destination}.${process.pid}.${Math.random().toString(16).slice(2)}.tmp`;
  try { fs.writeFileSync(temporary, JSON.stringify(snapshot, null, 2)); fs.renameSync(temporary, destination); }
  finally { try { fs.unlinkSync(temporary); } catch {} }
}

function fallback(input) {
  const parts = [input.model?.display_name || 'Claude'];
  if (typeof input.context_window?.used_percentage === 'number') parts.push(`${Math.round(input.context_window.used_percentage)}% context`);
  return parts.join(' | ') + '\n';
}

function run() {
  const raw = fs.readFileSync(0, 'utf8');
  let input;
  try { input = JSON.parse(raw); } catch { return; }
  try { writeSnapshot(normalize(input)); } catch {} // A widget failure must never interrupt Claude.
  let config;
  try { config = JSON.parse(fs.readFileSync(path.join(dataDir, 'bridge-config.json'), 'utf8')); } catch {}
  const previous = config?.previousStatusLine?.command;
  if (previous && !previous.includes('claude-bridge.cjs')) {
    const bashCandidates = [config.bashPath, 'C:/Program Files/Git/bin/bash.exe', 'C:/Program Files/Git/usr/bin/bash.exe'].filter(Boolean);
    const bash = bashCandidates.find(value => fs.existsSync(value));
    const result = spawnSync(bash || 'powershell.exe', bash ? ['-c', previous] : ['-NoProfile', '-NonInteractive', '-Command', previous], {
      input: raw, encoding: 'utf8', timeout: 2000, windowsHide: true, maxBuffer: 1024 * 1024,
    });
    if (result.status === 0 && result.stdout?.trim()) { process.stdout.write(result.stdout); return; }
  }
  process.stdout.write(fallback(input));
}

module.exports = { normalize, writeSnapshot, fallback };
if (require.main === module) { try { run(); } catch {} }
