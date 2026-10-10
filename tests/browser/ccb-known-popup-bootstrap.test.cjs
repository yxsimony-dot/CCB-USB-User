const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const scriptPath = path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbKnownPopupBootstrap.js');
const script = fs.readFileSync(scriptPath, 'utf8');
const origin = 'https://xc.b2b3.ccb.com';
const main = origin + '/NCCB/V6/b2bindex.jsp';
const target = origin + '/NCCB/NECV6B2BMainPlat_09_LH';
function page(url = main) {
    const dom = new JSDOM('<body></body>', { url, runScripts: 'outside-only' });
    const w = dom.window;
    const calls = [];
    const run = () => w.eval(`(${script})`)(child => { calls.push(child.document); });
    const add = (parent = w.document, tag = 'iframe', url = target) => {
        const frame = parent.createElement(tag);
        frame.src = url;
        parent.body.append(frame);
        frame.contentDocument.open(); frame.contentDocument.write('<body></body>'); frame.contentDocument.close();
        return frame;
    };
    const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
    return { dom, w, calls, run, add, flush };
}

test('walks legacy frame actual document even when declared src is stale', t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(p.w.document, 'frame', origin + '/NCCB/V6/b2bmainV7.jsp');
    f.contentWindow.history.replaceState({}, '', target);
    assert.match(f.getAttribute('src'), /b2bmainV7/);
    assert.equal(p.run(), 'installed');
    assert.ok(p.calls.includes(f.contentDocument));
});

test('discovers delayed iframe and descendants without timers', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    p.w.setTimeout = p.w.setInterval = () => { throw new Error('must not poll'); };
    p.run();
    const f = p.add(); const nested = p.add(f.contentDocument);
    await p.flush();
    assert.ok(p.calls.includes(nested.contentDocument));
});

test('frame load discovers replacement document while no business code waits', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(p.w.document, 'iframe', origin + '/NCCB/login.jsp');
    p.run(); p.calls.length = 0;
    f.src = target;
    f.contentDocument.open(); f.contentDocument.write('<body></body>'); f.contentDocument.close();
    f.dispatchEvent(new p.w.Event('load'));
    await p.flush();
    assert.ok(p.calls.includes(f.contentDocument));
});

test('repeat bootstrap remains idempotent but scans the current documents again', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add();
    assert.equal(p.run(), 'installed');
    p.calls.length = 0;
    assert.equal(p.run(), 'already-installed');
    assert.equal(p.calls.filter(d => d === f.contentDocument).length, 1);
    p.calls.length = 0;
    f.dispatchEvent(new p.w.Event('load')); f.dispatchEvent(new p.w.Event('load'));
    await p.flush();
    assert.equal(p.calls.filter(d => d === f.contentDocument).length, 1);
});

test('ignores unreadable, foreign origin and outside NCCB child documents', t => {
    const p = page(); t.after(() => p.dom.window.close());
    const unreadable = p.add();
    Object.defineProperty(unreadable, 'contentDocument', { get() { throw new Error('cross origin'); } });
    const foreign = p.add(p.w.document, 'iframe', 'https://other.example/NCCB/NECV6B2BMainPlat_09_LH');
    const outside = p.add(p.w.document, 'iframe', origin + '/other');
    const good = p.add();
    p.run();
    assert.ok(p.calls.includes(good.contentDocument));
    assert.ok(!p.calls.includes(foreign.contentDocument));
    assert.ok(!p.calls.includes(outside.contentDocument));
});

for (const url of ['http://xc.b2b3.ccb.com/NCCB/V6/b2bindex.jsp', 'https://xc.b2b3.ccb.com:8443/NCCB/V6/b2bindex.jsp',
    'https://user:password@xc.b2b3.ccb.com/NCCB/V6/b2bindex.jsp', 'https://xc.b2b3.ccb.com.example.com/NCCB/V6/b2bindex.jsp', origin + '/NCCB/other.jsp'])
test('rejects unapproved top URL ' + new URL(url).origin + new URL(url).pathname, t => {
    const p = page(url); t.after(() => p.dom.window.close());
    p.add(); assert.equal(p.run(), 'unsupported'); assert.equal(p.calls.length, 0);
});

test('does not bootstrap when invoked inside a child frame', t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(p.w.document, 'iframe', main);
    assert.equal(f.contentWindow.eval(`(${script})`)(() => p.calls.push(1)), 'unsupported');
    assert.equal(p.calls.length, 0);
});

test('limits traversal to four frame levels and sixteen documents per scan', t => {
    const p = page(); t.after(() => p.dom.window.close());
    let doc = p.w.document; const levels = [];
    for (let i = 0; i < 6; i++) { const f = p.add(doc); doc = f.contentDocument; levels.push(doc); }
    for (let i = 0; i < 20; i++) p.add();
    p.run();
    assert.ok(p.calls.includes(levels[3])); assert.ok(!p.calls.includes(levels[4]));
    assert.equal(p.calls.length, 16);
});

test('pagehide stops observers and frame loads until persisted pageshow resumes', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(); p.run();
    p.w.dispatchEvent(new p.w.PageTransitionEvent('pagehide', { persisted: true }));
    p.calls.length = 0;
    f.dispatchEvent(new p.w.Event('load')); const late = p.add();
    await p.flush(); assert.equal(p.calls.length, 0);
    p.w.dispatchEvent(new p.w.PageTransitionEvent('pageshow', { persisted: true }));
    await p.flush(); assert.ok(p.calls.includes(late.contentDocument));
});

test('installer errors in one document do not block sibling discovery', t => {
    const p = page(); t.after(() => p.dom.window.close());
    const bad = p.add(); const good = p.add(); const visited = [];
    const result = p.w.eval(`(${script})`)(child => {
        if (child.document === bad.contentDocument) throw new Error('unavailable');
        visited.push(child.document);
    });
    assert.equal(result, 'installed'); assert.ok(visited.includes(good.contentDocument));
});

test('removed frame load listener is detached and cannot trigger another scan', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(); p.run(); await p.flush();
    f.remove(); await p.flush(); p.calls.length = 0;
    f.dispatchEvent(new p.w.Event('load')); await p.flush();
    assert.equal(p.calls.length, 0);
});

test('does not traverse a child that has entered pagehide', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(); const nested = p.add(f.contentDocument); p.run(); await p.flush();
    f.contentWindow.dispatchEvent(new f.contentWindow.PageTransitionEvent('pagehide', { persisted: true }));
    p.calls.length = 0; p.run();
    assert.ok(!p.calls.includes(f.contentDocument));
    assert.ok(!p.calls.includes(nested.contentDocument));
});

test('ignores child URL credentials and stops acting after the top changes route', async t => {
    const p = page(); t.after(() => p.dom.window.close());
    const f = p.add(p.w.document, 'iframe', 'https://user:password@xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH');
    p.run(); assert.ok(!p.calls.includes(f.contentDocument));
    p.w.history.replaceState({}, '', origin + '/NCCB/other.jsp'); p.calls.length = 0;
    p.add(); await p.flush(); assert.equal(p.calls.length, 0);
});
