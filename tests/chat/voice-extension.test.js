const {test} = require('node:test');
const assert = require('node:assert/strict');
const {execute} = require('../../chrome-voice-extension/content.js');
const button = (label, click = () => {}, disabled = false) => ({disabled, getAttribute: n => n === 'aria-label' ? label : null, getClientRects: () => [1], click});
const doc = buttons => ({querySelectorAll: () => buttons()});
test('click start once and wait for active voice UI', async () => {
  let clicks = 0, active = false;
  const start = button('Start voice chat', () => { clicks++; active = true; });
  const result = await execute('start', doc(() => [active ? button('End voice chat') : start]), async () => {});
  assert.equal(result.code, 'VOICE_UI_ACTIVE'); assert.equal(clicks, 1);
});
test('active voice is not toggled off by another start request', async () => {
  const result = await execute('start', doc(() => [button('End voice chat', () => assert.fail('must not click'))]), async () => {});
  assert.equal(result.ok, true);
});
test('dictation and unknown microphones are never clicked', async () => {
  const result = await execute('start', doc(() => [button('Dictate', () => assert.fail()), button('Microphone', () => assert.fail())]), async () => {});
  assert.equal(result.code, 'VOICE_CONTROL_NOT_FOUND');
});
test('ambiguous controls fail closed', async () => {
  const result = await execute('start', doc(() => [button('Start voice chat', () => assert.fail()), button('Голосовой режим', () => assert.fail())]), async () => {});
  assert.equal(result.code, 'AMBIGUOUS_VOICE_CONTROL');
});
test('click without voice UI confirmation is not reported as success and is not repeated', async () => {
  let clicks = 0;
  const result = await execute('start', doc(() => [button('Start voice chat', () => clicks++)]), async () => {});
  assert.equal(result.ok, false); assert.equal(result.code, 'VOICE_NOT_CONFIRMED'); assert.equal(clicks, 1);
});
test('stop request waits for start control to return', async () => {
  let stopped = false;
  const result = await execute('stop', doc(() => [stopped ? button('Start voice chat') : button('End voice chat', () => { stopped = true; })]), async () => {});
  assert.equal(result.code, 'VOICE_UI_STOPPED');
});
test('cancellation before click has no microphone side effect', async () => {
  const result = await execute('start', doc(() => [button('Start voice chat', () => assert.fail())]), async () => {}, () => true);
  assert.equal(result.code, 'VOICE_CANCELLED');
});
test('cancellation after click requests stop when voice UI appears', async () => {
  let active = false, stopped = false, cancelled = false;
  const start = button('Start voice chat', () => { active = true; cancelled = true; });
  const end = button('End voice chat', () => { stopped = true; });
  const result = await execute('start', doc(() => [active ? end : start]), async () => {}, () => cancelled);
  assert.equal(result.code, 'VOICE_CANCELLED'); assert.equal(stopped, true);
});

test('expired command never starts a late microphone session', async () => {
  const result = await execute('start', doc(() => [button('Start voice chat', () => assert.fail())]), async () => {}, () => false, Date.now() - 1);
  assert.equal(result.code, 'VOICE_CANCELLED');
});

test('audio return requires active session followed by stable start control, not silence or mute', () => {
  const {createEndTracker} = require('../../chrome-voice-extension/content.js');
  const ended = createEndTracker();
  assert.equal(ended(false, true, 0), false);
  assert.equal(ended(false, true, 5000), false);
  assert.equal(ended(true, false, 6000), false);
  assert.equal(ended(true, false, 15000), false); // muted or silence: voice UI remains active
  assert.equal(ended(false, false, 20000), false); // UI loading, not confirmed ended
  assert.equal(ended(false, true, 21000), false);
  assert.equal(ended(false, true, 22000), false);
  assert.equal(ended(true, false, 22500), false); // transient disappearance resets debounce
  assert.equal(ended(false, true, 23000), false);
  assert.equal(ended(false, true, 24500), true);
});
