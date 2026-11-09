/* Only explicit commands from this extension can click a voice control. */
(() => {
  const normalized = s => (s || '').trim().toLocaleLowerCase().replace(/\s+/g, ' ');
  const starts = new Set(['start voice mode', 'start voice chat', 'start voice conversation', 'start voice',
    'voice mode', 'voice chat', 'начать голосовой чат', 'начать голосовой разговор', 'голосовой режим', 'начать голосовой режим', 'запустить голосовой режим']);
  const stops = new Set(['end voice chat', 'end voice conversation', 'end voice', 'stop voice chat', 'exit voice mode',
    'end conversation', 'завершить голосовой чат', 'завершить голосовой разговор', 'завершить разговор', 'выйти из голосового режима']);
  const visible = b => !b.disabled && b.getAttribute('aria-disabled') !== 'true' && b.getClientRects().length > 0;
  function classify(button) {
    // Do not use arbitrary chat text, an unlabelled microphone or dictation.
    const label = normalized(button.getAttribute('aria-label') || button.getAttribute('title'));
    if (stops.has(label)) return 'stop';
    if (starts.has(label)) return 'start';
    return null;
  }
  function controls(doc) {
    return Array.from(doc.querySelectorAll('button')).filter(visible).map(button => ({button, kind: classify(button)}));
  }
  async function execute(action, doc = document, pause = ms => new Promise(r => setTimeout(r, ms)), cancelled = () => false, expires = Date.now() + 30000) {
    if (!['start', 'stop'].includes(action)) return {ok: false, code: 'INVALID_COMMAND'};
    let clicked = false;
    for (let attempt = 0; attempt < 100; attempt++) {
      const found = controls(doc);
      const stop = found.filter(x => x.kind === 'stop');
      if (cancelled() || Date.now() >= expires) {
        if (!clicked) return {ok: false, code: 'VOICE_CANCELLED'};
        if (action === 'start' && stop.length === 1) {
          stop[0].button.click();
          return {ok: false, code: 'VOICE_CANCELLED'};
        }
        await pause(300);
        continue;
      }
      if (action === 'start' && stop.length === 1) return {ok: true, code: 'VOICE_UI_ACTIVE'};
      if (action === 'stop' && !stop.length && found.some(x => x.kind === 'start'))
        return {ok: true, code: 'VOICE_UI_STOPPED'};
      const candidates = found.filter(x => x.kind === (action === 'start' ? 'start' : 'stop'));
      if (!clicked && candidates.length === 1) {
        candidates[0].button.click();
        clicked = true;
      } else if (!clicked && candidates.length > 1) return {ok: false, code: 'AMBIGUOUS_VOICE_CONTROL'};
      await pause(300);
    }
    return {ok: false, code: clicked ? 'VOICE_NOT_CONFIRMED' : 'VOICE_CONTROL_NOT_FOUND'};
  }
  function createEndTracker() {
    let activeSeen = false, since = null;
    return (active, start, now) => {
      if (active) { activeSeen = true; since = null; return false; }
      if (!activeSeen || !start) { since = null; return false; }
      if (since === null) { since = now; return false; }
      return now - since >= 1500;
    };
  }
  if (typeof module !== 'undefined') module.exports = {classify, controls, execute, createEndTracker};
  if (typeof chrome === 'undefined' || !chrome.runtime?.onMessage) return;
  let operation = null;
  let sessionId = null, endTracker = createEndTracker();
  // A pause or muted microphone is not an ended conversation. Require the
  // explicit start control to return after an active voice UI was observed.
  setInterval(() => {
    if (!sessionId) return;
    const found = controls(document);
    if (!endTracker(found.some(x => x.kind === 'stop'), found.some(x => x.kind === 'start'), Date.now())) return;
    const id = sessionId; sessionId = null;
    chrome.runtime.sendMessage({type: 'wakemini_voice_ended', sessionId: id}).catch(() => {});
  }, 500);
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== chrome.runtime.id || location.origin !== 'https://chatgpt.com') return;
    if (message?.type === 'wakemini_voice_cancel') {
      if (operation?.id === message.id) operation.cancelled = true;
      respond({ok: true}); return;
    }
    if (message?.type !== 'wakemini_voice') return;
    if (!Number.isFinite(message.expires) || message.expires <= Date.now() || message.expires > Date.now() + 40000) {
      respond({ok: false, code: 'VOICE_CANCELLED'}); return;
    }
    if (operation) { respond({ok: false, code: 'VOICE_OPERATION_BUSY'}); return; }
    if (message.action === 'start') { sessionId = message.id; endTracker = createEndTracker(); }
    const current = {id: message.id, cancelled: false}; operation = current;
    execute(message.action, document, ms => new Promise(r => setTimeout(r, ms)), () => current.cancelled, message.expires)
      .then(respond, () => respond({ok: false, code: 'VOICE_PAGE_ERROR'})).finally(() => { operation = null; });
    return true;
  });
})();
