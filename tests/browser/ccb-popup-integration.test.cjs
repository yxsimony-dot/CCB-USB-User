// Exercise the same installer/bootstrap composition embedded by the WPF host.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const read = name => fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser', name), 'utf8');
const combined = `(() => { const install = (${read('CcbKnownPopupDismiss.js')}); install(); return (${read('CcbKnownPopupBootstrap.js')})(install); })()`;
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
const mobile = `<div class="mob_drain" type="page" id="synthetic-mask"></div>
<div class="mob_drain" id="synthetic-promo"><img src="https://b2bstatic3.ccb.com/V6/STY6/CN/images7/FN90001/mob_drain_close.png">
<div id="top_sjyl_type1">企业手机银行<input type="checkbox"></div></div>`;

function fixture() {
    const dom = new JSDOM('<frameset><frame src="/NCCB/V6/b2bmainV7.jsp"></frameset>', {
        url: 'https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp', runScripts: 'outside-only', pretendToBeVisual: true
    });
    const frame = dom.window.document.querySelector('frame');
    const child = frame.contentWindow;
    for (const w of [dom.window, child]) w.HTMLElement.prototype.getClientRects = () => [{ width: 100, height: 50 }];
    const messages = [];
    dom.window.chrome = { webview: { postMessage: m => messages.push(m) } };
    return { dom, frame, child, messages, run: () => dom.window.eval(combined) };
}

for (const route of ['/NCCB/NECV6B2BMainPlat_09_LH', '/NCCB/NECV6B2BMainPlat_08_LH'])
test('legacy frame load installs and closes promotion once: ' + route, async t => {
    const p = fixture(); t.after(() => p.dom.window.close());
    assert.equal(p.run(), 'installed');
    p.child.history.replaceState(null, '', route);
    p.child.document.open(); p.child.document.write(mobile); p.child.document.close();
    let clicks = 0;
    const close = p.child.document.querySelector('img');
    close.addEventListener('click', () => {
        clicks++;
        for (const element of p.child.document.querySelectorAll('.mob_drain')) element.style.display = 'none';
    });
    p.frame.dispatchEvent(new p.dom.window.Event('load'));
    p.child.document.dispatchEvent(new p.child.Event('DOMContentLoaded'));
    await flush();
    assert.equal(clicks, 1);
    assert.equal(p.child.document.querySelector('#synthetic-mask').style.display, 'none');
    assert.equal(p.child.document.querySelector('input').checked, false);
    assert.ok(p.messages.includes('usb-launcher-popup:mobile-clicked'));
    p.run(); p.frame.dispatchEvent(new p.dom.window.Event('load')); await flush();
    assert.equal(clicks, 1);
});

for (const route of ['/NCCB/NECV6B2BMainPlat_09_LH', '/NCCB/NECV6B2BMainPlat_08_LH'])
test('no popup leaves page usable and later insertion closes independently: ' + route, async t => {
    const p = fixture(); t.after(() => p.dom.window.close());
    p.child.history.replaceState(null, '', route);
    p.child.document.open(); p.child.document.write('<button id="business">业务</button>'); p.child.document.close();
    let businessClicks = 0;
    p.child.document.querySelector('#business').onclick = () => businessClicks++;
    p.run(); p.child.document.dispatchEvent(new p.child.Event('DOMContentLoaded')); await flush();
    p.child.document.querySelector('#business').click();
    assert.equal(businessClicks, 1);
    assert.ok(!p.messages.includes('usb-launcher-popup:mobile-clicked'));
    p.child.document.body.insertAdjacentHTML('beforeend', mobile);
    let promotionClicks = 0;
    p.child.document.querySelector('img').onclick = () => promotionClicks++;
    await flush();
    assert.equal(promotionClicks, 1);
    assert.equal(businessClicks, 1);
});
