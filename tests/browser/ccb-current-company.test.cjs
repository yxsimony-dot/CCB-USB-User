const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const scriptPath = path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbCurrentCompany.js');
const source = fs.readFileSync(scriptPath, 'utf8');
const origin = 'https://b2b3.ccb.com';
const company = '示例物业服务有限公司清远分公司工会';
const account = '1234567890120007';
const home = (name = company) => `<div class="accInfoDiv"><div class="accDetailDiv"><div class="accInfo"><span id="ACCNM_ZHGS" class="deptName">${name}</span><span id="ACCNO_ZHGS" class="accNo">${account}</span></div></div></div>`;
const result = (name = company) => `<form name="jhform"><table id="headTable" class="tableConfirm"><tr><td class="cfmTitle cfmSty1">账　号：</td><td class="cfmInfo cfmSty4 acc">${account}</td><td class="cfmTitle cfmSty1">账户名称：</td><td class="cfmInfo cfmSty4 fzWord">${name}</td></tr></table><input type="hidden" name="checkedAccno" value="${account}"><input type="hidden" name="checkedAccName" value="${name}"></form>`;
function page(t, html = home(), url = origin + '/NCCB/V6/b2bindex.jsp') {
    const dom = new JSDOM(html, { url, runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    function visible(doc) {
        Object.defineProperty(doc.defaultView.Element.prototype, 'getClientRects', { configurable: true, value() { return [{ width: 100, height: 20 }]; } });
    }
    visible(w.document);
    const addFrame = (html, parent = w.document) => {
        const frame = parent.createElement('iframe'); parent.body.append(frame);
        frame.contentDocument.body.innerHTML = html; visible(frame.contentDocument);
        return frame;
    };
    const run = (request = { origin }) => {
        return JSON.parse(JSON.stringify(w.eval(`(${source})`)(request)));
    };
    return { w, doc: w.document, dom, addFrame, run };
}

test('returns only complete company name without account or exporter state', t => {
    const p = page(t);
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
    assert.equal(p.doc.__usbWebLauncherStatementExportV1, undefined);
});

test('reads company after dynamic population without storing prior identity', t => {
    const p = page(t, home(''));
    assert.deepEqual(p.run(), { status: 'waiting' });
    p.doc.querySelector('#ACCNM_ZHGS').textContent = company;
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
    p.doc.body.innerHTML = '';
    assert.deepEqual(p.run(), { status: 'waiting' });
});

test('restricts request origin and page to approved HTTPS bank NCCB locations', t => {
    for (const url of ['https://b2bbank.ccb.com/NCCB/V6/b2bindex.jsp', 'https://example.com/NCCB/home', 'http://b2b3.ccb.com/NCCB/home', origin + ':8443/NCCB/home', origin + '/other/home']) {
        assert.deepEqual(page(t, home(), url).run({ origin: new URL(url).origin }), { status: 'unsupported' });
    }
    const p = page(t);
    for (const request of [undefined, {}, { origin: 'https://xc.b2b3.ccb.com' }, { origin: origin + '/' }]) {
        assert.deepEqual(p.run(request === undefined ? null : request), { status: 'unsupported' });
    }
    const xc = 'https://xc.b2b3.ccb.com';
    assert.deepEqual(page(t, home(), xc + '/NCCB/home').run({ origin: xc }), { status: 'ready', companyName: company });
});

test('uses only verified homepage shape and requires unmasked account companion', t => {
    for (const mutate of [
        p => p.doc.querySelector('.accInfoDiv').className = 'other',
        p => p.doc.querySelector('#ACCNO_ZHGS').remove(),
        p => p.doc.querySelector('#ACCNO_ZHGS').textContent = '123***0007',
        p => p.doc.querySelector('#ACCNM_ZHGS').id = 'other'
    ]) {
        const p = page(t); mutate(p); assert.deepEqual(p.run(), { status: 'waiting' });
    }
});

test('preserves company suffix and rejects literal truncation or control characters', t => {
    for (const name of ['示例公司…', '示例公司...', '示例\u0001公司', '示例\u202E公司', '示例\n公司']) {
        const p = page(t, home()); p.doc.querySelector('#ACCNM_ZHGS').textContent = name;
        assert.deepEqual(p.run(), { status: 'waiting' });
    }
    assert.deepEqual(page(t, home('  ' + company + '  ')).run(), { status: 'ready', companyName: company });
    const p = page(t, home('示例公司…')); p.doc.querySelector('#ACCNM_ZHGS').title = company;
    assert.deepEqual(p.run(), { status: 'waiting' });
});

test('ignores hidden identities and hidden frame branches', t => {
    for (const attribute of ['hidden', 'style="display:none"', 'style="visibility:hidden"', 'style="opacity:0"']) {
        const p = page(t, `<section ${attribute}>${home('隐藏公司')}</section>`);
        const frame = p.addFrame(home('另一隐藏公司')); frame.hidden = true;
        assert.deepEqual(p.run(), { status: 'waiting' });
    }
});

test('blocks stale company while any visible login form is present', t => {
    const p = page(t, home() + '<form><input type="password" id="LOG_PWD"></form>');
    assert.deepEqual(p.run(), { status: 'waiting' });
    p.doc.querySelector('form').hidden = true;
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
    p.addFrame('<form><input type="password"></form>');
    assert.deepEqual(p.run(), { status: 'waiting' });
});

test('reads nested visible same-origin inherited frames', t => {
    const p = page(t, '');
    const first = p.addFrame(''); p.addFrame(home(), first.contentDocument);
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
});

test('checks actual frame document origin and NCCB path after navigation', t => {
    for (const [url, expected] of [
        [origin + '/NCCB/NECV6B2BMainPlat_09_LH', { status: 'ready', companyName: company }],
        [origin + '/other/frame', { status: 'waiting' }],
        ['https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH', { status: 'waiting' }]
    ]) {
        const p = page(t, '');
        const child = page(t, home(), url);
        const frame = p.addFrame('');
        frame.setAttribute('src', origin + '/NCCB/V6/b2bmainV7.jsp');
        // Model a previously navigated frame; each child remains a real jsdom document.
        Object.defineProperty(frame, 'contentDocument', { get: () => child.doc });
        assert.deepEqual(p.run(), expected);
    }
});

test('rejects cross-origin declared frames without reading their content', t => {
    const p = page(t, ''); const frame = p.addFrame(home('外域公司'));
    frame.setAttribute('src', 'https://example.com/frame');
    Object.defineProperty(frame, 'contentDocument', { get() { throw new Error('cross-origin'); } });
    assert.deepEqual(p.run(), { status: 'waiting' });
});

test('accepts consistent result identity without any export invocation', t => {
    assert.deepEqual(page(t, result()).run(), { status: 'ready', companyName: company });
});

test('rejects result identity if hidden cross-checks or structural evidence disagree', t => {
    for (const mutate of [
        p => p.doc.querySelector('[name=checkedAccName]').value = '另一公司',
        p => p.doc.querySelector('[name=checkedAccno]').value = '999999999',
        p => p.doc.querySelector('[name=checkedAccName]').remove(),
        p => p.doc.querySelector('[name=checkedAccno]').after(p.doc.querySelector('[name=checkedAccno]').cloneNode(true)),
        p => p.doc.querySelector('#headTable').after(p.doc.querySelector('#headTable').cloneNode(true)),
        p => p.doc.querySelector('.fzWord').hidden = true,
        p => p.doc.querySelectorAll('td')[2].textContent = '其他名称：',
        p => p.doc.querySelector('form').name = 'other'
    ]) {
        const p = page(t, result()); mutate(p);
        assert.deepEqual(p.run(), { status: 'waiting' });
    }
});

test('deduplicates same company and rejects different visible companies', t => {
    const p = page(t); p.addFrame(result());
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
    p.addFrame(home('另一有限公司'));
    assert.deepEqual(p.run(), { status: 'ambiguous' });
});

test('uses valid homepage when result identity is unavailable', t => {
    const p = page(t); p.addFrame(result('截断公司…'));
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
});

test('fails closed when document or nesting budget could hide another company', t => {
    const p = page(t); for (let i = 0; i < 17; i++) p.addFrame('');
    assert.deepEqual(p.run(), { status: 'ambiguous' });
    const q = page(t); let doc = q.doc;
    for (let i = 0; i < 6; i++) doc = q.addFrame('', doc).contentDocument;
    assert.deepEqual(q.run(), { status: 'ambiguous' });
});

test('never reads whole-page text, clicks, submits, fetches, or mutates DOM', t => {
    const p = page(t, home() + '<form><button>查询</button></form>');
    const before = p.doc.documentElement.outerHTML;
    const forbidden = () => { throw new Error('forbidden page operation'); };
    for (const node of [p.doc.body, p.doc.documentElement]) Object.defineProperty(node, 'textContent', { get: forbidden });
    p.w.HTMLElement.prototype.click = forbidden;
    p.w.HTMLFormElement.prototype.submit = forbidden;
    p.w.HTMLFormElement.prototype.requestSubmit = forbidden;
    p.w.fetch = forbidden;
    p.w.XMLHttpRequest = forbidden;
    assert.deepEqual(p.run(), { status: 'ready', companyName: company });
    assert.equal(p.doc.documentElement.outerHTML, before);
});
