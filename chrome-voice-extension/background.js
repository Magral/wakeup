const HOST = 'ru.wakemini.voice';
let port = null, busy = false;
let knownTab = null;
let voiceSessionId = null;
const restored = chrome.storage.session.get(['knownTab', 'voiceSessionId']).then(saved => { knownTab = saved.knownTab ?? null; voiceSessionId = saved.voiceSessionId ?? null; });
async function remember(id) { knownTab = id; await chrome.storage.session.set({knownTab: id}); return id; }
const done = new Map();
const cancelled = new Set();
async function prepareTab(action) {
  await restored;
  let tabs = await chrome.tabs.query({url: 'https://chatgpt.com/*'});
  // Stop only the tab previously used by this extension. Never guess another conversation.
  if (action === 'stop') {
    if (knownTab === null || !tabs.some(t => t.id === knownTab)) return null;
    return knownTab;
  }
  if (knownTab !== null && tabs.some(t => t.id === knownTab)) return knownTab;
  if (tabs.length > 1) throw new Error('MULTIPLE_CHATGPT_TABS');
  if (tabs.length === 1) return remember(tabs[0].id);
  const windows = await chrome.windows.getAll({windowTypes: ['normal']});
  if (windows.length) {
    const tab = await chrome.tabs.create({windowId: windows[0].id, url: 'https://chatgpt.com/', active: false});
    return remember(tab.id);
  }
  const window = await chrome.windows.create({url: 'https://chatgpt.com/', focused: false, state: 'minimized'});
  return remember(window.tabs[0].id);
}
async function handle(message) {
  if (message?.action === 'cancel' && /^[0-9a-f]{32}$/.test(message.id || '')) {
    cancelled.add(message.id);
    while (cancelled.size > 64) cancelled.delete(cancelled.values().next().value);
    if (knownTab !== null) chrome.tabs.sendMessage(knownTab, {type: 'wakemini_voice_cancel', id: message.id}).catch(() => {});
    return;
  }
  if (!message || !/^[0-9a-f]{32}$/.test(message.id || '') || !['start', 'stop'].includes(message.action)) return;
  if (done.has(message.id)) { port?.postMessage(done.get(message.id)); return; }
  if (busy) { port?.postMessage({id: message.id, ok: false, code: 'VOICE_OPERATION_BUSY'}); return; }
  busy = true;
  if (message.action === 'start') { await restored; voiceSessionId = message.id; await chrome.storage.session.set({voiceSessionId}); }
  const expires = Date.now() + 35000;
  let result;
  try {
    const tabId = await prepareTab(message.action);
    if (tabId === null) result = {ok: false, code: 'NO_MANAGED_VOICE_TAB'};
    else {
      for (let n = 0; n < 40; n++) {
        if (Date.now() >= expires || cancelled.has(message.id)) { result = {ok: false, code: 'VOICE_CANCELLED'}; break; }
        try {
          result = await chrome.tabs.sendMessage(tabId, {type: 'wakemini_voice', action: message.action, id: message.id, expires});
          if (result) break;
        } catch { /* Content script is not ready on a newly loaded page. */ }
        await new Promise(r => setTimeout(r, 250));
      }
      result ||= {ok: false, code: 'VOICE_PAGE_UNAVAILABLE'};
    }
  } catch (error) {
    result = {ok: false, code: error?.message === 'MULTIPLE_CHATGPT_TABS' ? error.message : 'VOICE_BACKGROUND_ERROR'};
  } finally { busy = false; }
  // Only fixed status codes cross the native bridge; no chat text, cookies or page HTML.
  const reply = {id: message.id, ok: result.ok === true, code: String(result.code || '').slice(0, 80)};
  done.set(message.id, reply);
  while (done.size > 64) done.delete(done.keys().next().value);
  port?.postMessage(reply);
}
// An alarm revives a suspended worker after the helper has been restarted.
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'wakemini-reconnect') connect(); });
chrome.alarms.create('wakemini-reconnect', {periodInMinutes: 0.5});
function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
    port.onMessage.addListener(handle);
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      port = null;
      setTimeout(connect, 2000);
    });
  } catch { port = null; setTimeout(connect, 2000); }
}
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();

chrome.runtime.onMessage.addListener(async (message, sender) => {
  await restored;
  if (sender.id !== chrome.runtime.id || sender.tab?.id !== knownTab ||
      !sender.url?.startsWith('https://chatgpt.com/') || message?.type !== 'wakemini_voice_ended' ||
      message.sessionId !== voiceSessionId || !voiceSessionId) return;
  const sessionId = voiceSessionId; voiceSessionId = null;
  await chrome.storage.session.set({voiceSessionId: null});
  port?.postMessage({event: 'voice_ended', sessionId});
});
chrome.tabs.onRemoved.addListener(async tabId => {
  await restored;
  if (tabId !== knownTab || !voiceSessionId) return;
  const sessionId = voiceSessionId; voiceSessionId = null;
  await chrome.storage.session.set({voiceSessionId: null});
  port?.postMessage({event: 'voice_ended', sessionId});
});
