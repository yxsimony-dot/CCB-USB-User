const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbKnownPopupDismiss.js'), 'utf8');
const markup = `<div id="noticeTip" class="ccbWin"><div class="ccbWinBoder"><div class="ccbWinHead"><span>温馨提示</span><a class="ccbWinColse" href="javascript:void(0)">×</a></div><div class="ccbWinBody"><input type="checkbox">不再提醒</div></div></div>`;
function page(options = {}) {
    const origin = options.origin || 'https://xc.b2b3.ccb.com';
    const dom = new JSDOM('<body></body>', { url: options.topUrl || origin + '/NCCB/V6/b2bindex.jsp', runScripts: 'outside-only', pretendToBeVisual: true });
    dom.window.HTMLElement.prototype.getClientRects = function () { return [{ width: 100, height: 20 }]; };
    const frame = dom.window.document.createElement('iframe');
    frame.src = options.url || origin + '/NCCB/NECV6B2BMainPlat_09_LH';
    dom.window.document.body.append(frame);
    const w = options.topOnly ? dom.window : frame.contentWindow;
    w.document.open();
    w.document.write('<body>' + (options.empty ? '' : markup) + '</body>');
    w.document.close();
    // jsdom has no layout engine; retain its real DOM, computed styles and events.
    w.HTMLElement.prototype.getClientRects = function () { return [{ width: 100, height: 20 }]; };
    let clicks = 0;
    const messages = [];
    w.chrome = { webview: { postMessage: message => messages.push(message) } };
    w.document.addEventListener('click', event => {
        if (event.target.matches('a.ccbWinColse')) {
            event.preventDefault();
            clicks++;
            event.target.closest('.ccbWin').style.display = 'none';
        }
    });
    const run = () => w.eval(`(${script})()`);
    const ready = async () => { w.document.dispatchEvent(new w.Event('DOMContentLoaded')); await flush(); };
    const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };
    return { dom, w, frame, run, ready, flush, messages, get clicks() { return clicks; } };
}

test('returns immediately when absent and closes only a later matching notice', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    p.w.setTimeout = p.w.setInterval = () => { throw new Error('must not poll'); };
    assert.equal(typeof p.run(), 'string');
    await p.ready();
    assert.equal(p.clicks, 0);
    p.w.document.body.insertAdjacentHTML('beforeend', markup);
    await p.flush();
    assert.equal(p.clicks, 1);
    assert.deepEqual(p.messages, ['usb-launcher-popup:notice-installed', 'usb-launcher-popup:notice-clicked']);
});

test('closes an existing notice through its original click handler without changing checkbox', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const checkbox = p.w.document.querySelector('input');
    p.run(); await p.ready();
    assert.equal(p.clicks, 1);
    assert.equal(checkbox.checked, false);
    assert.equal(p.w.document.querySelector('#noticeTip').style.display, 'none');
});

test('waits for visibility changes without timers and recloses a notice shown again', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const notice = p.w.document.querySelector('#noticeTip');
    notice.style.display = 'none';
    p.run(); await p.ready(); assert.equal(p.clicks, 0);
    notice.style.display = 'block'; await p.flush(); assert.equal(p.clicks, 1);
    notice.style.display = 'block'; await p.flush(); assert.equal(p.clicks, 2);
});

test('installation is idempotent and permits a genuinely new notice control', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    p.run(); p.run(); await p.ready(); assert.equal(p.clicks, 1);
    p.w.document.body.innerHTML = markup;
    await p.flush(); assert.equal(p.clicks, 2);
});

for (const [name, change] of [
    ['unknown modal', d => { d.querySelector('#noticeTip').id = 'ESafeDownlodWin'; }],
    ['business confirmation title', d => { d.querySelector('span').textContent = '业务确认'; }],
    ['wrong close class', d => { d.querySelector('a').className = 'closeModal'; }],
    ['duplicate notice ID', d => { d.body.insertAdjacentHTML('beforeend', markup); }],
    ['duplicate close control', d => { d.querySelector('.ccbWinHead').append(d.querySelector('a').cloneNode(true)); }],
    ['duplicate title', d => { d.querySelector('.ccbWinHead').append(d.querySelector('span').cloneNode(true)); }],
    ['hidden notice', d => { d.querySelector('#noticeTip').hidden = true; }],
    ['hidden ancestor', d => { d.body.style.visibility = 'hidden'; }],
    ['disabled close', d => { d.querySelector('a').setAttribute('disabled', ''); }],
    ['aria disabled close', d => { d.querySelector('a').setAttribute('aria-disabled', 'true'); }]
]) test('does not click ' + name, async t => {
    const p = page(); t.after(() => p.dom.window.close());
    change(p.w.document); p.run(); await p.ready(); assert.equal(p.clicks, 0);
});

for (const [name, options] of [
    ['other bank host', { origin: 'https://b2bbank.ccb.com' }],
    ['suffix spoofed host', { origin: 'https://b2b3.ccb.com.example.com' }],
    ['HTTP', { origin: 'http://b2b3.ccb.com' }],
    ['wrong port', { origin: 'https://b2b3.ccb.com:8443' }],
    ['outside bank application', { url: 'https://xc.b2b3.ccb.com/other/page.html' }],
    ['other top document', { topUrl: 'https://xc.b2b3.ccb.com/NCCB/other.jsp' }],
    ['different top origin', { topUrl: 'https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp' }],
    ['credentials in frame URL', { url: 'https://user:password@xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH' }],
    ['credentials in top URL', { topUrl: 'https://user:password@xc.b2b3.ccb.com/NCCB/V6/b2bindex.jsp' }],
    ['top level notice document', { topOnly: true, topUrl: 'https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH' }]
]) test('does not operate in ' + name, async t => {
    const p = page(options); t.after(() => p.dom.window.close());
    p.run(); await p.ready(); assert.equal(p.clicks, 0);
});

test('also supports the exact b2b3 bank host', async t => {
    const p = page({ origin: 'https://b2b3.ccb.com' }); t.after(() => p.dom.window.close());
    p.run(); await p.ready(); assert.equal(p.clicks, 1);
});

test('disconnects on pagehide and resumes safely on BFCache pageshow', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    p.run(); await p.ready();
    p.w.dispatchEvent(new p.w.PageTransitionEvent('pagehide', { persisted: true }));
    p.w.document.body.insertAdjacentHTML('beforeend', markup);
    await p.flush(); assert.equal(p.clicks, 0);
    p.w.dispatchEvent(new p.w.PageTransitionEvent('pageshow', { persisted: true }));
    await p.flush(); assert.equal(p.clicks, 1);
    assert.equal(p.messages.filter(m => m === 'usb-launcher-popup:notice-installed').length, 1);
});

test('first scan lets subsequent DOMContentLoaded handlers bind the original close action', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    let originalHandlerCalls = 0;
    p.run();
    p.w.document.addEventListener('DOMContentLoaded', () => {
        p.w.document.querySelector('a').onclick = () => { originalHandlerCalls++; };
    }, { once: true });
    await p.ready();
    assert.equal(originalHandlerCalls, 1);
});

test('does not click a notice inside a hidden parent frame', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    p.frame.style.display = 'none';
    p.run(); await p.ready(); assert.equal(p.clicks, 0);
});

// Captured bank structure: both the empty mask and content use .mob_drain;
// the close image has no ID/class. Only its original handler clears both.
const mobileImageRoot = 'https://b2bstatic3.ccb.com/V6/STY6/CN/images7/FN90001/';
const mobileMarkup = `<div class="mob_drain" type="page"></div>
<div class="mob_drain"><div id="befADV1"><img src="${mobileImageRoot}mob_drain_01.png"><img src="${mobileImageRoot}mob_drain_close.png"></div>
<div id="befADV2"><div id="top_sjyl_type1"><img src="${mobileImageRoot}mob_drain_07.png"><a href="javascript:void(0)">服务介绍</a></div>
<div id="top_sjyl_type2" style="display:none"><a href="javascript:void(0)">服务介绍</a></div>
<div id="top_sjyl_type3" style="display:none"><a href="javascript:void(0)">立即开通</a></div></div>
<input type="checkbox">不再提醒</div>`;
function addMobile(p) {
    p.w.document.body.insertAdjacentHTML('beforeend', mobileMarkup);
    const panels = [...p.w.document.querySelectorAll('div.mob_drain')];
    const panel = panels[1];
    const close = panel.querySelector('img[src$="mob_drain_close.png"]');
    const checkbox = panel.querySelector('input');
    let closeCalls = 0;
    let otherClicks = 0;
    close.onclick = () => {
        closeCalls++;
        panels.forEach(element => { element.style.display = 'none'; });
    };
    panel.querySelectorAll('a,input').forEach(element => {
        element.addEventListener('click', () => { otherClicks++; });
    });
    return { panels, panel, close, checkbox, get closeCalls() { return closeCalls; }, get otherClicks() { return otherClicks; } };
}

for (const checked of [false, true]) test('closes mobile promotion without notice and preserves checkbox ' + checked, async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const m = addMobile(p); m.checkbox.checked = checked;
    p.w.setTimeout = p.w.setInterval = () => { throw new Error('must not poll'); };
    p.run(); await p.ready();
    assert.equal(m.closeCalls, 1);
    assert.equal(m.otherClicks, 0);
    assert.equal(m.checkbox.checked, checked);
    assert.ok(m.panels.every(element => element.style.display === 'none'));
    assert.ok(p.messages.includes('usb-launcher-popup:mobile-clicked'));
});

test('handles both known popup types independently in one document', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const m = addMobile(p);
    p.run(); await p.ready();
    assert.equal(p.clicks, 1);
    assert.equal(m.closeCalls, 1);
});

for (const origin of ['https://b2b3.ccb.com', 'https://xc.b2b3.ccb.com']) {
    for (const route of ['/NCCB/NECV6B2BMainPlat_08_LH', '/NCCB/NECV6B2BMainPlat_09', '/NCCB/new-home']) {
        test('known notices work across bank homepage routes: ' + origin + route, async t => {
            const p = page({ origin, url: origin + route }); t.after(() => p.dom.window.close());
            const m = addMobile(p);
            p.w.document.querySelector('#noticeTip input').checked = true;
            m.checkbox.checked = false;
            p.run(); await p.ready();
            assert.equal(p.clicks, 1);
            assert.equal(m.closeCalls, 1);
            assert.equal(m.otherClicks, 0);
            assert.equal(p.w.document.querySelector('#noticeTip input').checked, true);
            assert.equal(m.checkbox.checked, false);
        });
    }
}

test('a business confirmation on the 08 homepage is still left to the user', async t => {
    const p = page({ url: 'https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH' });
    t.after(() => p.dom.window.close());
    p.w.document.querySelector('.ccbWinHead span').textContent = '业务确认';
    p.run(); await p.ready();
    assert.equal(p.clicks, 0);
});

test('mobile promotion appearing after installation is closed without polling', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    p.run(); await p.ready();
    const m = addMobile(p); await p.flush();
    assert.equal(m.closeCalls, 1);
});

test('hidden mobile template closes when shown and closes again after reappearing', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const m = addMobile(p);
    m.panels.forEach(element => { element.style.display = 'none'; });
    p.run(); await p.ready(); assert.equal(m.closeCalls, 0);
    m.panels.forEach(element => { element.style.display = 'block'; });
    await p.flush(); assert.equal(m.closeCalls, 1);
    m.panels.forEach(element => { element.style.display = 'block'; });
    await p.flush(); assert.equal(m.closeCalls, 2);
});

for (const [name, change] of [
    ['wrong image host', m => { m.close.src = 'https://example.com/V6/STY6/CN/images7/FN90001/mob_drain_close.png'; }],
    ['spoofed image host', m => { m.close.src = mobileImageRoot.replace('b2bstatic3.ccb.com', 'b2bstatic3.ccb.com.example.com') + 'mob_drain_close.png'; }],
    ['wrong image path', m => { m.close.src = mobileImageRoot + 'mob_drain_07.png'; }],
    ['HTTP image', m => { m.close.src = mobileImageRoot.replace('https:', 'http:') + 'mob_drain_close.png'; }],
    ['image credentials', m => { m.close.src = mobileImageRoot.replace('https://', 'https://user:secret@') + 'mob_drain_close.png'; }],
    ['image port', m => { m.close.src = mobileImageRoot.replace('.com/', '.com:8443/') + 'mob_drain_close.png'; }],
    ['duplicate close image', m => { m.close.parentElement.append(m.close.cloneNode(true)); }],
    ['duplicate content panel', m => { m.panel.parentElement.append(m.panel.cloneNode(true)); }],
    ['duplicate marker', m => { m.panel.append(m.panel.querySelector('#top_sjyl_type1').cloneNode(true)); }],
    ['missing marker', m => { m.panel.querySelector('#top_sjyl_type1').remove(); }],
    ['wrong panel class', m => { m.panel.className = 'business-confirmation'; }],
    ['hidden content panel', m => { m.panel.hidden = true; }],
    ['hidden close image', m => { m.close.hidden = true; }],
    ['disabled close image', m => { m.close.setAttribute('aria-disabled', 'true'); }]
]) test('does not close mobile promotion with ' + name + ' but still handles notice', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const m = addMobile(p); change(m);
    p.run(); await p.ready();
    assert.equal(m.closeCalls, 0);
    assert.equal(p.clicks, 1);
    assert.equal(p.messages.includes('usb-launcher-popup:mobile-clicked'), false);
});

test('does not close mobile promotion in another host', async t => {
    const p = page({ empty: true, origin: 'https://example.com' }); t.after(() => p.dom.window.close());
    const m = addMobile(p);
    p.run(); await p.ready(); assert.equal(m.closeCalls, 0);
});

test('does not close mobile promotion inside a hidden parent frame', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const m = addMobile(p); p.frame.hidden = true;
    p.run(); await p.ready(); assert.equal(m.closeCalls, 0);
});

test('unsupported notice does not prevent closing the mobile promotion', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const m = addMobile(p);
    p.w.document.querySelector('.ccbWinHead span').textContent = '业务确认';
    p.run(); await p.ready();
    assert.equal(p.clicks, 0);
    assert.equal(m.closeCalls, 1);
});

test('top-frame bootstrap can install into the target frame with its own DOM and observer', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const install = p.dom.window.eval(`(${script})`);
    assert.equal(install(p.w), 'installed');
    await p.ready();
    const m = addMobile(p); await p.flush();
    assert.equal(m.closeCalls, 1);
    assert.equal(p.dom.window.document.__usbWebLauncherKnownPopupDismissV1, undefined);
});

test('reports fixed popup events through the top bridge without duplicating child messages', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    addMobile(p);
    const topMessages = [];
    p.dom.window.chrome = { webview: { postMessage: message => topMessages.push(message) } };
    p.run(); await p.ready();
    assert.deepEqual(topMessages, ['usb-launcher-popup:notice-installed', 'usb-launcher-popup:notice-clicked', 'usb-launcher-popup:mobile-clicked']);
    assert.deepEqual(p.messages, []);
});

test('falls back to the child bridge when the top bridge throws', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    addMobile(p);
    p.dom.window.chrome = { webview: { postMessage: () => { throw new Error('bridge unavailable'); } } };
    p.run(); await p.ready();
    assert.deepEqual(p.messages, ['usb-launcher-popup:notice-installed', 'usb-launcher-popup:notice-clicked', 'usb-launcher-popup:mobile-clicked']);
});

const guideImageRoot = 'https://b2bstatic3.ccb.com/V6/STY6/CN/images6/FN000F1/';
function addGuide(p, step = 1) {
    const steps = [1, 2, 3];
    p.w.document.body.insertAdjacentHTML('beforeend', `<div id="show1"><div class="area_content"><div class="head_img">${steps.map(n => `<img id="Newstep${n}" usemap="#Map${n}" src="${guideImageRoot}Newstep${n - 1}.png" style="display:${n === step ? 'block' : 'none'}">`).join('')}</div>${steps.map(n => `<map id="Map${n}" name="Map${n}"><area href="#" onclick="closeMengBan();"><area href="#this" onclick="next();"></map>`).join('')}<div id="background"></div></div></div>`);
    const panel = p.w.document.querySelector('#show1');
    let closes = 0, next = 0;
    panel.querySelectorAll('area').forEach(area => {
        // Image map areas have no layout rectangles even while the image is visible.
        area.getClientRects = () => [];
        area.onclick = event => {
            event.preventDefault();
            if (area.getAttribute('onclick') === 'closeMengBan();') {
                closes++; panel.style.display = 'none';
            } else next++;
        };
    });
    return { panel, image: panel.querySelector('#Newstep' + step), close: panel.querySelector('#Map' + step + ' area'), get closes() { return closes; }, get next() { return next; } };
}
function addPromotion(p) {
    p.w.document.body.insertAdjacentHTML('beforeend', `<div id="myModal" class="modal"><div class="modal-content"><img src="https://b2bstatic3.ccb.com/V6/STY6/CN/images6/login_new/jtkd_image_png@1x.png"><span id="closeModal" class="closeModal">×</span><a href="#apply">立即申请</a></div></div>`);
    const panel = p.w.document.querySelector('#myModal');
    const close = panel.querySelector('#closeModal');
    let closes = 0, applications = 0;
    close.addEventListener('click', () => { closes++; panel.style.display = 'none'; });
    panel.querySelector('a').onclick = event => { event.preventDefault(); applications++; };
    return { panel, close, image: panel.querySelector('img'), get closes() { return closes; }, get applications() { return applications; } };
}
for (const step of [1, 2, 3]) test('closes verified image-map guide step ' + step + ' using original handler', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const g = addGuide(p, step); p.run(); await p.ready();
    assert.equal(g.closes, 1); assert.equal(g.next, 0);
    assert.equal(g.panel.style.display, 'none');
    assert.ok(p.messages.includes('usb-launcher-popup:guide-clicked'));
});
test('closes verified loan promotion with addEventListener close without applying', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const m = addPromotion(p); assert.equal(m.close.onclick, null);
    p.run(); await p.ready();
    assert.equal(m.closes, 1); assert.equal(m.applications, 0);
    assert.ok(p.messages.includes('usb-launcher-popup:promotion-clicked'));
});
for (const add of [addGuide, addPromotion]) test('known panel can be closed again after its handler hid it: ' + add.name, async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const popup = add(p); p.run(); await p.ready(); assert.equal(popup.closes, 1);
    popup.panel.style.display = 'block'; await p.flush(); assert.equal(popup.closes, 2);
});
for (const [name, change] of [
    ['foreign guide image', g => { g.image.src = g.image.src.replace('b2bstatic3.ccb.com', 'example.com'); }],
    ['unrecognized guide art', g => { g.image.src = guideImageRoot + 'confirmation.png'; }],
    ['wrong image-map link', g => { g.image.setAttribute('usemap', '#Map2'); }],
    ['business confirmation handler', g => { g.close.setAttribute('onclick', 'confirmTransaction();'); }],
    ['duplicate guide', g => { g.panel.parentElement.append(g.panel.cloneNode(true)); }],
    ['hidden guide', g => { g.panel.hidden = true; }],
    ['missing mask', g => { g.panel.querySelector('#background').remove(); }],
    ['disabled close area', g => { g.close.setAttribute('disabled', ''); }]
]) test('does not close ' + name, async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const g = addGuide(p); change(g); p.run(); await p.ready(); assert.equal(g.closes, 0);
});
for (const [name, change] of [
    ['unknown modal image', m => { m.image.src = 'https://b2bstatic3.ccb.com/business-confirmation.png'; }],
    ['foreign modal image', m => { m.image.src = m.image.src.replace('b2bstatic3.ccb.com', 'example.com'); }],
    ['duplicate modal', m => { m.panel.parentElement.append(m.panel.cloneNode(true)); }],
    ['duplicate modal close', m => { m.close.parentElement.append(m.close.cloneNode(true)); }],
    ['disabled modal close', m => { m.close.setAttribute('aria-disabled', 'true'); }],
    ['hidden modal', m => { m.panel.hidden = true; }]
]) test('does not click ' + name, async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const m = addPromotion(p); change(m); p.run(); await p.ready(); assert.equal(m.closes, 0);
});
test('a guide close that mutates DOM without hiding does not create a mutation loop', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    const g = addGuide(p); let attempts = 0;
    g.close.onclick = event => { event.preventDefault(); attempts++; g.panel.className = 'attempt-' + attempts; };
    p.run(); await p.ready(); await p.flush();
    assert.equal(attempts, 1);
    g.panel.className = 'unrelated-change'; await p.flush(); assert.equal(attempts, 1);
});
test('all four known overlays close independently when inserted after installation', async t => {
    const p = page({ empty: true }); t.after(() => p.dom.window.close());
    p.run(); await p.ready();
    p.w.document.body.insertAdjacentHTML('beforeend', markup);
    const m = addMobile(p), g = addGuide(p), loan = addPromotion(p);
    await p.flush();
    assert.equal(p.clicks, 1); assert.equal(m.closeCalls, 1); assert.equal(g.closes, 1); assert.equal(loan.closes, 1);
});
