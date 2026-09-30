'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const dataDir = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AIUsageWidget');
const settingsPath = path.join(os.homedir(), '.claude', 'settings.json');
const configPath = path.join(dataDir, 'bridge-config.json');

fs.mkdirSync(dataDir, { recursive: true });
const original = fs.existsSync(settingsPath) ? fs.readFileSync(settingsPath, 'utf8') : '{}';
const settings = JSON.parse(original.replace(/^\uFEFF/, ''));
const priorConfig = fs.existsSync(configPath) ? JSON.parse(fs.readFileSync(configPath, 'utf8')) : null;

if (process.argv.includes('--remove')) {
  if (priorConfig && settings.statusLine?.command === priorConfig.installedCommand) {
    if (priorConfig.previousStatusLine === null) delete settings.statusLine;
    else settings.statusLine = priorConfig.previousStatusLine;
    fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2) + '\n');
    console.log('Restored the previous Claude status line; other settings were preserved.');
  } else console.log('Claude settings were left unchanged because the status line has changed since setup.');
  process.exit(0);
}

const bridge = path.join(__dirname, 'claude-bridge.cjs').replaceAll('\\', '/');
const command = `"${process.execPath.replaceAll('\\', '/')}" "${bridge}"`;
const alreadyInstalled = settings.statusLine?.command?.includes('claude-bridge.cjs');
if (alreadyInstalled && !priorConfig) throw new Error('Existing bridge has no backup; refusing to overwrite it.');
const previous = alreadyInstalled ? priorConfig.previousStatusLine : (settings.statusLine ?? null);
if (!alreadyInstalled) {
  const backup = path.join(dataDir, `claude-settings-before-widget-${new Date().toISOString().replaceAll(':', '-')}.json`);
  fs.writeFileSync(backup, original, { flag: 'wx' });
}
const config = { previousStatusLine: previous, installedCommand: command };
fs.writeFileSync(configPath, JSON.stringify(config, null, 2));
settings.statusLine = { ...(settings.statusLine || {}), type: 'command', command };
fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
const temp = settingsPath + '.ai-usage-widget.tmp';
fs.writeFileSync(temp, JSON.stringify(settings, null, 2) + '\n');
fs.renameSync(temp, settingsPath);
console.log('Claude usage feed connected. Previous status line and all other settings preserved.');
