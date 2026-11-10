const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/PsbcCertificateLogin.js'), 'utf8');

function page(url = 'https://corpebank.psbc.com/#/login') {
    const dom = new JSDOM(`<div id="tab-ukeyLogin" aria-selected="true">UKey</div>
        <div id="pane-ukeyLogin"><form>
        <div class="el-input"><input value="TEST-CUSTOMER"><span class="el-input__prefix">&#23458;&#25143;&#32534;&#21495;</span></div>
        <div class="el-input"><input value="TEST-USER"><span class="el-input__prefix">&#30331; &#24405; &#21517;</span></div>
        <div class="vx-pwd"><div class="pwd-prefix"><span>&#30331;&#24405;&#23494;&#30721;</span></div>
        <div id="ukeyLogin"><div id="ukeyLogin_div" tabindex="0" isbusy="0"><div id="ukeyLogin_input"></div></div></div></div>
        <button type="button" class="loginbtn">&#30331;&#24405;</button></form></div>`,
        { url, runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    w.HTMLElement.prototype.getClientRects = function () { return [{ width: 100, height: 30 }]; };
    let focus = true, clicks = 0, captures = 0;
    w.document.hasFocus = () => focus;
    w.g_input_connectFlag = 1;
    w.g_stopSendRequest = 1;
    const password = w.document.querySelector('#ukeyLogin_div');
    const mask = w.document.querySelector('#ukeyLogin_input');
    const button = w.document.querySelector('button');
    password.addEventListener('focus', () => {
        captures++; password.setAttribute('isbusy', '1'); w.g_stopSendRequest = 0;
    });
    password.addEventListener('blur', () => {
        password.setAttribute('isbusy', '0'); w.g_stopSendRequest = 1;
    });
    button.addEventListener('click', () => { clicks++; });
    const run = w.eval(`(${script})`);
    const call = (operation, extra = {}) => run({ operation, ticket: 'test-ticket', length: 8, ...extra }).status;
    return { w, password, mask, button, call,
        get clicks() { return clicks; }, get captures() { return captures; },
        loseFocus() { focus = false; },
        fill() { mask.textContent = '\u25cf'.repeat(8); } };
}

test('activates the bank control, waits for secure mask and clicks once without a script PIN', () => {
    const p = page();
    assert.equal(p.call('inspect'), 'ready');
    assert.equal(p.call('prepare'), 'focused');
    assert.equal(p.call('check'), 'empty');
    p.fill();
    assert.equal(p.call('check'), 'filled');
    assert.equal(p.call('submit'), 'submitted');
    assert.equal(p.clicks, 1);
    assert.equal(p.captures, 1);
    assert.equal(p.w.g_stopSendRequest, 1);
    assert.notEqual(p.call('submit'), 'submitted');
    assert.notEqual(p.call('inspect', { ticket: 'new-ticket' }), 'ready');
});

for (const url of ['http://corpebank.psbc.com/#/login', 'https://corpebankq.psbc.com/#/login',
    'https://corpebankcdn.psbc.com/dist/index.html#/login', 'https://corpebank.psbc.com.evil.invalid/#/login',
    'https://corpebank.psbc.com:444/#/login', 'https://user@corpebank.psbc.com/#/login',
    'https://corpebank.psbc.com/#/index', 'https://corpebank.psbc.com/#/login?tab=userLogin',
    'https://corpebank.psbc.com/?redirect=x#/login', 'https://corpebank.psbc.com/#/login?tab=ukeyLogin&tab=userLogin',
    'https://corpebank.psbc.com/#https://other.invalid/login', 'https://corpebank.psbc.com/#//other.invalid/login']) {
    test(`rejects non-professional-login address ${url}`, () => {
        const p = page(url);
        assert.equal(p.call('inspect'), 'unsupported');
        assert.equal(p.captures, 0);
    });
}
test('allows explicit professional tab', () => {
    assert.equal(page('https://corpebank.psbc.com/#/login?tab=ukeyLogin').call('inspect'), 'ready');
});
for (const mutate of [
    p => p.loseFocus(),
    p => { p.w.document.querySelector('input').value = 'CHANGED'; },
    p => { p.w.document.querySelector('#tab-ukeyLogin').setAttribute('aria-selected', 'false'); },
    p => { p.w.location.hash = '/index'; },
    p => { p.password.blur(); },
    p => { p.button.disabled = true; },
    p => { p.button.classList.add('is-loading'); },
    p => { p.mask.textContent += '*'; },
    p => { p.mask.textContent = 'not-a-mask'; },
    p => { p.w.document.querySelector('form').style.display = 'none'; },
    p => { p.password.replaceWith(p.password.cloneNode(true)); }
]) {
    test('changed page/account/focus/input prevents submission', () => {
        const p = page();
        p.call('inspect'); p.call('prepare'); p.fill(); mutate(p);
        assert.notEqual(p.call('submit'), 'submitted');
        assert.equal(p.clicks, 0);
    });
}
test('never overwrites manual input or restarts an input attempt', () => {
    const p = page();
    p.fill();
    assert.equal(p.call('inspect'), 'unsupported');
    p.mask.textContent = '';
    p.call('inspect'); p.call('prepare');
    assert.notEqual(p.call('prepare'), 'focused');
    assert.notEqual(p.call('inspect', { ticket: 'another' }), 'ready');
    assert.equal(p.captures, 1);
});
test('incomplete secure input does not submit', () => {
    const p = page();
    p.call('inspect'); p.call('prepare');
    p.mask.textContent = '*';
    assert.equal(p.call('check'), 'pending');
    assert.equal(p.call('submit'), 'failed');
    assert.equal(p.clicks, 0);
});
test('missing service and empty account wait without starting capture', () => {
    const p = page();
    p.w.g_input_connectFlag = 0;
    assert.equal(p.call('inspect'), 'waiting');
    p.w.g_input_connectFlag = 1;
    p.w.document.querySelector('input').value = '';
    assert.equal(p.call('inspect'), 'waiting');
    assert.equal(p.captures, 0);
});
test('release only blurs the ticketed control, even after account or route change', () => {
    const p = page();
    p.call('inspect'); p.call('prepare');
    p.w.location.hash = '/index';
    p.call('release', { ticket: 'wrong' });
    assert.equal(p.w.g_stopSendRequest, 0);
    p.call('release');
    assert.equal(p.w.g_stopSendRequest, 1);
});
test('blur callback changing account prevents button click', () => {
    const p = page();
    p.call('inspect'); p.call('prepare'); p.fill();
    p.password.addEventListener('blur', () => { p.w.document.querySelector('input').value = 'OTHER'; });
    assert.equal(p.call('submit'), 'failed');
    assert.equal(p.clicks, 0);
});
test('blur callback replacing account nodes prevents button click', () => {
    const p = page();
    p.call('inspect'); p.call('prepare'); p.fill();
    p.password.addEventListener('blur', () => {
        const account = p.w.document.querySelector('input');
        account.replaceWith(account.cloneNode(true));
    });
    assert.equal(p.call('submit'), 'failed');
    assert.equal(p.clicks, 0);
});
test('duplicate secure controls are rejected', () => {
    const p = page();
    p.password.parentElement.append(p.password.cloneNode(true));
    assert.equal(p.call('inspect'), 'unsupported');
    assert.equal(p.captures, 0);
});
