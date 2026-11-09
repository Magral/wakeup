const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
function harness(tabs, saved = {}) {
  const sent = [], created = [], replies = [], alarms = [];
  let onEvent, onRemoved;
  const chrome = {
    storage: {session: {get: async () => saved, set: async x => Object.assign(saved, x)}},
    tabs: {onRemoved: {addListener: fn => {onRemoved = fn;}}, query: async () => tabs, create: async opts => { created.push(opts); return {id: 42}; },
      sendMessage: async (id, message) => { sent.push({id, message}); return {ok:true, code:message.action === 'start' ? 'VOICE_UI_ACTIVE' : 'VOICE_UI_STOPPED'}; }},
    windows: {getAll: async () => [{id: 9}], create: async () => assert.fail('must use existing window')},
    runtime: {id: 'test', onMessage: {addListener: fn => {onEvent = fn;}}, connectNative: () => ({postMessage: x => replies.push(x), onMessage:{addListener:()=>{}}, onDisconnect:{addListener:()=>{}}}),
      onStartup:{addListener:()=>{}}, onInstalled:{addListener:()=>{}}},
    alarms:{onAlarm:{addListener:()=>{}}, create:(...args) => alarms.push(args)}
  };
  const context = vm.createContext({chrome, setTimeout, Date});
  vm.runInContext(fs.readFileSync('chrome-voice-extension/background.js','utf8'), context);
  return {run: message => {context.message = message; return vm.runInContext('handle(message)', context);}, sent, created, replies, alarms, event: (msg, tabId = 1) => onEvent(msg, {id: "test", url: "https://chatgpt.com/", tab: {id: tabId}}), removed: id => onRemoved(id)};
}
const id = 'a'.repeat(32);
test('creates only inactive tab, sends bounded command, restores stop target across worker restart', async () => {
  const h = harness([], {});
  await h.run({id, action:'start'});
  assert.equal(h.created[0].active, false);
  assert.equal(h.sent[0].id, 42);
  assert.ok(h.sent[0].message.expires <= Date.now() + 35000);
  assert.equal(h.replies[0].code, 'VOICE_UI_ACTIVE');
  assert.equal(h.alarms[0][0], 'wakemini-reconnect');
  const restarted = harness([{id:42}], {knownTab:42});
  await restarted.run({id, action:'stop'});
  assert.equal(restarted.sent[0].id, 42);
  assert.equal(restarted.replies[0].code, 'VOICE_UI_STOPPED');
});
test('multiple unknown tabs are not guessed or activated', async () => {
  const h = harness([{id:1},{id:2}]); await h.run({id, action:'start'});
  assert.equal(h.replies[0].code, 'MULTIPLE_CHATGPT_TABS');
  assert.equal(h.sent.length, 0); assert.equal(h.created.length, 0);
});
test('duplicate request ID returns stored reply without another page command', async () => {
  const h = harness([{id:1}]); await h.run({id, action:'start'}); await h.run({id, action:'start'});
  assert.equal(h.sent.length, 1); assert.equal(h.replies.length, 2);
});
test('stop without owned target never clicks another conversation', async () => {
  const h = harness([{id:1}]); await h.run({id, action:'stop'});
  assert.equal(h.replies[0].code, 'NO_MANAGED_VOICE_TAB'); assert.equal(h.sent.length, 0);
});

test('only the managed current session can return audio, exactly once', async () => {
  const h = harness([{id:1}]); await h.run({id, action:'start'});
  await h.event({type:'wakemini_voice_ended', sessionId:id}, 2);
  await h.event({type:'wakemini_voice_ended', sessionId:'b'.repeat(32)});
  assert.equal(h.replies.length, 1);
  await h.event({type:'wakemini_voice_ended', sessionId:id});
  await h.event({type:'wakemini_voice_ended', sessionId:id});
  assert.equal(h.replies.length, 2);
  assert.equal(h.replies[1].event, 'voice_ended');
});
test('closing managed conversation sends ended event', async () => {
  const h = harness([{id:1}]); await h.run({id, action:'start'});
  await h.removed(2); assert.equal(h.replies.length, 1);
  await h.removed(1); assert.equal(h.replies[1].sessionId, id);
});
