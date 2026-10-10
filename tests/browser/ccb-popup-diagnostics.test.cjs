// Synthetic DOM only; this probe must never change a bank page or collect credentials.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const sourcePath = path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbPopupDiagnostics.js');
const script = fs.readFileSync(sourcePath, 'utf8');
const base = 'https://xc.b2b3.ccb.com/NCCB/home.jsp?session=SECRET_QUERY#SECRET_HASH';
function page(html = '', url = base) {
    const dom = new JSDOM(html, { url, runScripts: 'outside-only' });
    dom.window.HTMLElement.prototype.getClientRects = function () { return [{ width: 20, height: 20 }]; };
    return { dom, w: dom.window, run: () => JSON.parse(JSON.stringify(dom.window.eval(`(${script})()`))) };
}
test('returns synchronously with no popup and does not install observers or timers', () => {
    const p = page('<main><p>账户内容</p></main>');
    for (const name of ['MutationObserver', 'setTimeout', 'setInterval', 'postMessage']) {
        p.w[name] = () => { throw Error(`must not use ${name}`); };
    }
    const result = p.run();
    assert.equal(result.status, 'diagnostic');
    assert.equal(result.documents.length, 1);
    assert.deepEqual(result.documents[0].candidates, []);
    p.w.close();
});
test('collects fixed popup markers and structural close targets without modifying DOM', () => {
    const p = page('<div id="mobile-dialog"><h2>企业手机银行，您的助手</h2><button id="dismiss" title="关闭" onclick="bank.closeNotice(\'PRIVATE_ARG\')">×</button></div><section class="popup"><b>温馨提示</b></section><div class="modal"><span>受益所有人信息备案的通知</span></div>');
    let clicked = 0;
    p.w.document.querySelector('button').addEventListener('click', () => clicked++);
    const before = p.w.document.documentElement.outerHTML;
    const result = p.run();
    assert.equal(result.status, 'diagnostic');
    const candidates = result.documents[0].candidates;
    for (const marker of ['mobile-banking', 'warm-notice', 'beneficial-owner-notice']) {
        assert.ok(candidates.some(c => c.markers.includes(marker)), marker);
    }
    const close = candidates.find(c => c.id === 'dismiss');
    assert.equal(close.closeCandidate, true);
    assert.equal(close.onclickFunction, 'bank.closeNotice');
    assert.equal(close.document, 0);
    assert.ok(close.ancestors.length <= 4);
    assert.equal(clicked, 0);
    assert.equal(p.w.document.documentElement.outerHTML, before);
    assert.equal(JSON.stringify(result).includes('PRIVATE_ARG'), false);
    p.w.close();
});
test('never reads values, cookies, storage, HTML or non-marker page text into output', () => {
    const p = page('<div class="popup"><h2>温馨提示 PRIVATE_TITLE</h2><input value="PRIVATE_PIN"><p>PRIVATE_ACCOUNT</p><button class="close" title="PRIVATE_TITLE" onclick="dismiss(\'PRIVATE_ARGUMENT\')">关闭 PRIVATE_BUTTON</button></div>');
    for (const [object, prop] of [[p.w.HTMLInputElement.prototype, 'value'], [p.w.document, 'cookie'], [p.w, 'localStorage'], [p.w, 'sessionStorage'], [p.w.Element.prototype, 'outerHTML'], [p.w.Element.prototype, 'innerHTML']]) {
        Object.defineProperty(object, prop, { configurable: true, get() { throw Error(`private read ${prop}`); } });
    }
    const result = p.run();
    assert.equal(result.status, 'diagnostic');
    const serialized = JSON.stringify(result);
    for (const secret of ['PRIVATE_', 'SECRET_QUERY', 'SECRET_HASH', '?session=', '企业手机银行', '温馨提示']) assert.equal(serialized.includes(secret), false, secret);
    p.w.close();
});
test('redacts long digits and truncates structural attributes', () => {
    const p = page(`<div class="popup-${'x'.repeat(220)}" id="notice-123456789012"><span>温馨提示</span></div>`);
    const candidates = p.run().documents[0].candidates;
    assert.ok(candidates.length > 0);
    assert.equal(JSON.stringify(candidates).includes('123456789012'), false);
    for (const c of candidates) { assert.ok(c.class.length <= 160); assert.ok(c.id.length <= 160); }
    p.w.close();
});
test('reports hidden structures as invisible', () => {
    const p = page('<div class="popup" style="display:none"><span>温馨提示</span><button class="close">×</button></div>');
    const candidates = p.run().documents[0].candidates;
    assert.ok(candidates.length > 0);
    assert.ok(candidates.every(c => c.visible === false));
    p.w.close();
});
test('inspects same-origin NCCB child documents while stripping frame URL queries', () => {
    const p = page('<iframe id="home" src="/NCCB/child.jsp?ticket=PRIVATE_FRAME"></iframe>');
    const child = page('<div class="popup"><b>温馨提示</b><button class="close">×</button></div>', 'https://xc.b2b3.ccb.com/NCCB/child.jsp?ticket=PRIVATE_FRAME');
    Object.defineProperty(p.w.document.querySelector('iframe'), 'contentDocument', { get: () => child.w.document });
    const result = p.run();
    assert.equal(result.documents.length, 2);
    assert.ok(result.documents[1].candidates.some(c => c.markers.includes('warm-notice')));
    assert.equal(result.documents[1].candidates[0].document, 1);
    assert.equal(result.documents[0].frames[0].address.path, '/NCCB/child.jsp');
    assert.equal(JSON.stringify(result).includes('PRIVATE_FRAME'), false);
    p.w.close(); child.w.close();
});
test('does not access cross-origin frame documents even on another ccb host', () => {
    const p = page('<iframe src="https://www.ccb.com/NCCB/notice.jsp?PRIVATE_CROSS"></iframe><iframe src="https://outside.example/page?PRIVATE_CROSS"></iframe>');
    // jsdom's UA stylesheet :focus matching itself reads iframe.contentDocument.
    // Supply frame layout here so the spy measures the probe, not that jsdom implementation.
    const realStyle = p.w.getComputedStyle.bind(p.w);
    p.w.getComputedStyle = element => element.tagName === 'IFRAME' ? { display: 'block', visibility: 'visible' } : realStyle(element);
    let accessed = false;
    for (const frame of p.w.document.querySelectorAll('iframe')) Object.defineProperty(frame, 'contentDocument', { get() { accessed = true; throw Error('cross origin'); } });
    const result = p.run();
    assert.equal(result.status, 'diagnostic');
    assert.equal(result.documents.length, 1);
    assert.equal(result.documents[0].frames.length, 2);
    assert.equal(accessed, false);
    assert.equal(JSON.stringify(result).includes('PRIVATE_CROSS'), false);
    p.w.close();
});
test('stops nested document traversal at four child levels', () => {
    const chain = Array.from({ length: 7 }, (_, i) => page('<iframe src="/NCCB/next"></iframe>', `https://xc.b2b3.ccb.com/NCCB/level${i}`));
    for (let i = 0; i < chain.length - 1; i++) Object.defineProperty(chain[i].w.document.querySelector('iframe'), 'contentDocument', { get: () => chain[i + 1].w.document });
    const result = chain[0].run();
    assert.equal(result.documents.length, 5);
    assert.equal(result.documents[4].path, '/NCCB/level4');
    for (const p of chain) p.w.close();
});
test('refuses child documents redirected to other origins or non-NCCB paths', () => {
    const p = page('<iframe src="/NCCB/same"></iframe><iframe src="/NCCB/other"></iframe>');
    const children = [page('<button class="close">×</button>', 'https://other.ccb.com/NCCB/page'), page('<button class="close">×</button>', 'https://xc.b2b3.ccb.com/outside')];
    [...p.w.document.querySelectorAll('iframe')].forEach((frame, i) => Object.defineProperty(frame, 'contentDocument', { get: () => children[i].w.document }));
    assert.equal(p.run().documents.length, 1);
    p.w.close(); for (const child of children) child.w.close();
});
test('rejects non-HTTPS, nonstandard ports, domain lookalikes and paths outside NCCB', () => {
    for (const url of ['http://ccb.com/NCCB/home', 'https://ccb.com:444/NCCB/home', 'https://ccb.com.evil.example/NCCB/home', 'https://evilccb.com/NCCB/home', 'https://ccb.com/other/home']) {
        const p = page('<div class="popup"><span>温馨提示</span></div>', url);
        const result = p.run();
        assert.equal(result.status, 'unsupported', url);
        assert.deepEqual(result.documents, []);
        p.w.close();
    }
});
test('accepts the exact ccb host and explicit HTTPS port 443', () => {
    const p = page('', 'https://ccb.com:443/NCCB/home');
    assert.equal(p.run().status, 'diagnostic');
    p.w.close();
});
test('bounds popup candidates and total inspected documents', () => {
    const p = page(Array.from({ length: 50 }, (_, i) => `<button id="close${i}" class="close">×</button><iframe src="/NCCB/frame${i}"></iframe>`).join(''));
    const children = [];
    for (const frame of p.w.document.querySelectorAll('iframe')) {
        const child = page('<button class="close">×</button>', frame.src); children.push(child);
        Object.defineProperty(frame, 'contentDocument', { get: () => child.w.document });
    }
    const result = p.run();
    assert.equal(result.documents.length, 16);
    assert.equal(result.documents[0].candidates.length, 30);
    p.w.close(); for (const child of children) child.w.close();
});

test('inspects javascript-declared frames by actual safe document without executing the source', () => {
    const p = page('<iframe id="legacy"></iframe>');
    const frame = p.w.document.querySelector('iframe');
    const originalGet = frame.getAttribute.bind(frame);
    frame.getAttribute = name => name === 'src' ? 'javascript:PRIVATE_SCRIPT()' : originalGet(name);
    p.w.PRIVATE_SCRIPT = () => { throw Error('must not execute source'); };
    const child = page('<div class="mob_drain"><h2>企业手机银行</h2><i class="ccbWinColse"></i></div>', 'https://xc.b2b3.ccb.com/NCCB/mobile');
    Object.defineProperty(frame, 'contentDocument', { get: () => child.w.document });
    const result = p.run();
    assert.equal(result.documents.length, 2);
    assert.equal(result.documents[0].frames[0].srcKind, 'javascript');
    assert.equal(result.documents[0].frames[0].inspection, 'inspected');
    assert.equal(result.documents[0].frames[0].address, null);
    assert.equal(JSON.stringify(result).includes('PRIVATE_SCRIPT'), false);
    assert.ok(result.documents[1].candidates.some(c => c.class === 'ccbWinColse' && c.closeCandidate));
    p.w.close(); child.w.close();
});

test('inspects accessible inherited blank and srcdoc documents and propagates frame visibility', () => {
    const p = page('<iframe style="display:none"></iframe><iframe srcdoc="PRIVATE_HTML"></iframe>');
    const children = ['about:blank', 'about:srcdoc'].map(url => page('<section class="mob_drain"><b>企业手机银行</b><i class="colse"></i></section>', url));
    [...p.w.document.querySelectorAll('iframe')].forEach((frame, i) => Object.defineProperty(frame, 'contentDocument', { get: () => children[i].w.document }));
    const result = p.run();
    assert.equal(result.documents.length, 3);
    assert.deepEqual(result.documents[0].frames.map(f => f.srcKind), ['blank', 'srcdoc']);
    assert.equal(result.documents[1].path, 'about:blank');
    assert.equal(result.documents[2].path, 'about:srcdoc');
    assert.equal(result.documents[1].origin, 'https://xc.b2b3.ccb.com');
    assert.ok(result.documents[1].candidates.every(c => !c.visible));
    assert.equal(JSON.stringify(result).includes('PRIVATE_HTML'), false);
    p.w.close(); children.forEach(child => child.w.close());
});

test('reports frame inspection reasons while rejecting inaccessible or unsafe actual documents', () => {
    const p = page('<iframe src="http://xc.b2b3.ccb.com/NCCB/http"></iframe><iframe></iframe><iframe></iframe><iframe></iframe><iframe src="data:text/html,PRIVATE_DATA"></iframe>');
    const frames = [...p.w.document.querySelectorAll('iframe')];
    const realStyle = p.w.getComputedStyle.bind(p.w);
    p.w.getComputedStyle = element => element.tagName === 'IFRAME' ? { display: 'block', visibility: 'visible' } : realStyle(element);
    const foreign = page('<div class="mob_drain"></div>', 'https://outside.example/NCCB/private');
    Object.defineProperty(frames[0], 'contentDocument', { get() { throw Error('must not access declared HTTP'); } });
    Object.defineProperty(frames[1], 'contentDocument', { get: () => foreign.w.document });
    Object.defineProperty(frames[2], 'contentDocument', { get() { throw Error('cross origin access denied'); } });
    Object.defineProperty(frames[3], 'contentDocument', { get: () => null });
    Object.defineProperty(frames[4], 'contentDocument', { get() { throw Error('must not access data'); } });
    const result = p.run();
    assert.equal(result.documents.length, 1);
    assert.deepEqual(result.documents[0].frames.map(f => f.inspection), ['blocked-declared', 'blocked-document', 'unavailable', 'unavailable', 'blocked-declared']);
    assert.deepEqual(result.documents[0].frames.map(f => f.srcKind), ['http', 'blank', 'blank', 'blank', 'other']);
    assert.equal(JSON.stringify(result).includes('PRIVATE_DATA'), false);
    p.w.close(); foreign.w.close();
});

test('records bounded hidden mobile panel structure, sanitized images and misspelled close handlers', () => {
    const p = page('<div class="mob_drain" style="display:none"><section id="top_sjyl_type1"><div><div><div><div><h2>企业手机银行 PRIVATE_TITLE</h2></div></div></div></div><img class="icon" src="/NCCB/close.png?PRIVATE_IMAGE#PRIVATE_HASH" onclick="ccbWinColse(\'PRIVATE_ARGUMENT\')"><i class="colse"></i><input id="PRIVATE_INPUT" value="PRIVATE_PIN">' + Array.from({ length: 90 }, (_, i) => `<span id="node-${i}">PRIVATE_CONTENT</span>`).join('') + '</section></div>');
    const before = p.w.document.documentElement.outerHTML;
    p.w.ccbWinColse = () => { throw Error('must not execute handler'); };
    const result = p.run();
    const panels = result.documents[0].panels;
    assert.ok(panels.some(panel => panel.class === 'mob_drain'));
    assert.ok(panels.some(panel => panel.id === 'top_sjyl_type1'));
    const nodes = panels.flatMap(panel => panel.nodes);
    assert.ok(nodes.length <= 60);
    assert.ok(nodes.every(node => !node.visible));
    const image = nodes.find(node => node.tag === 'img');
    assert.equal(image.onclickFunction, 'ccbWinColse');
    assert.equal(image.closeCandidate, true);
    assert.deepEqual(image.image, { origin: 'https://xc.b2b3.ccb.com', path: '/NCCB/close.png' });
    assert.ok(result.documents[0].candidates.some(c => c.class === 'colse' && c.closeCandidate));
    assert.equal(JSON.stringify(result).includes('PRIVATE_'), false);
    assert.equal(p.w.document.documentElement.outerHTML, before);
    p.w.close();
});

test('associates a mobile title with an unnamed region up to six ancestors away', () => {
    const p = page('<article id="promotion"><img id="unknown-icon" src="/NCCB/icon.png"><div><div><div><div><div><b>企业手机银行</b></div></div></div></div></div></article>');
    const panels = p.run().documents[0].panels;
    assert.ok(panels.some(panel => panel.id === 'promotion' && panel.nodes.some(node => node.id === 'unknown-icon')));
    p.w.close();
});
