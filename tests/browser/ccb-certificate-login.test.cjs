// Run with Node and jsdom 26.1.0 on NODE_PATH. Only synthetic credentials are used.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbCertificateLogin.js'), 'utf8');
const url = 'https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN?TXCODE=N7N010&LOGIN_TYPE=UC';
const fixture = `<form id="form_id" name="jhform" method="post" action="/NCCB/MNECV6B2BMainPlat_UN">
<h2>证书登录</h2>
<table id="Wrap_searchbar"><tbody><tr><td>
<input id="CUSTOMERID" name="CUSTOMERID" value="TEST-CUSTOMER">
<input id="USERID" name="USERID" value="TEST-OPERATOR">
<input id="LOG_PWD" name="LOG_PWD" type="password" placeholder="登录密码">
<input id="LOGIN_BOX_BTN" type="button" value="登 录" class="btn_js_link1" disabled>
</td></tr></tbody></table></form><input id="Usr_Pswd" type="password">`;
function supplyLayout(w) {
    // jsdom provides DOM/events, but no layout engine. Supply layout only.
    w.HTMLElement.prototype.getClientRects = function () {
        for (let e = this; e; e = e.parentElement) {
            const style = w.getComputedStyle(e);
            if (style.display === 'none' || style.visibility === 'hidden') return [];
        }
        return [{ width: 200, height: 30 }];
    };
}
function page(options = {}) {
    const dom = new JSDOM(fixture, { url: options.url || url, runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    supplyLayout(w);
    const field = w.document.querySelector('#LOG_PWD');
    const button = w.document.querySelector('#LOGIN_BOX_BTN');
    let clicks = 0;
    let submittedPassword;
    if (options.enable !== false) {
        for (const type of ['input', 'change']) field.addEventListener(type, () => {
            button.disabled = !field.value.trim() || !w.document.querySelector('#USERID').value.trim() ||
                !w.document.querySelector('#CUSTOMERID').value.trim();
        });
    }
    button.addEventListener('click', () => { clicks++; submittedPassword = field.value; });
    const run = w.eval(`(${script})`);
    const request = { origin: new URL(url).origin, path: new URL(url).pathname, ticket: 'synthetic-document-ticket' };
    return { dom, w, field, button, run, request,
        inspect: () => run({ ...request, operation: 'inspect' }),
        submit: () => run({ ...request, operation: 'submit', pin: 'Test123!' }),
        get clicks() { return clicks; }, get submittedPassword() { return submittedPassword; } };
}
test('fills the certificate password, uses page input handling, and clicks once', () => {
    const p = page();
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    assert.equal(p.submittedPassword, 'Test123!');
    assert.equal(p.w.document.querySelector('#Usr_Pswd').value, '');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
});

test('selects the certificate jhform when the other login mode shares its form ID', () => {
    const p = page();
    const primary = p.field.form;
    primary.action = '/NCCB/NECV6B2BMainPlat';
    primary.insertAdjacentHTML('afterend', '<form id="form_id" name="jhform2" method="post" action="/NCCB/NECV6B2BMainPlat"><h2>手机号/用户名登录</h2></form>');
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.submittedPassword, 'Test123!');
    assert.equal(p.clicks, 1);
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    p.dom.window.close();
});

test('scopes certificate controls to jhform despite hidden duplicates in another login mode', () => {
    const p = page();
    const alternate = p.field.form.cloneNode(true);
    alternate.name = 'jhform2';
    alternate.style.display = 'none';
    alternate.querySelector('h2').textContent = '手机号/用户名登录';
    // Put alternate mode first: document-wide ID lookups must not pick its controls.
    p.field.form.before(alternate);
    let alternateClicks = 0;
    alternate.querySelector('#LOGIN_BOX_BTN').addEventListener('click', () => alternateClicks++);
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    assert.equal(p.submittedPassword, 'Test123!');
    assert.equal(alternate.querySelector('#LOG_PWD').value, '');
    assert.equal(alternateClicks, 0);
    p.dom.window.close();
});

test('refuses two certificate jhform forms instead of guessing by document order', () => {
    const p = page();
    const duplicate = p.field.form.cloneNode(true);
    p.field.form.after(duplicate);
    let duplicateClicks = 0;
    duplicate.querySelector('#LOGIN_BOX_BTN').addEventListener('click', () => duplicateClicks++);
    assert.equal(p.inspect().status, 'unsupported');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.field.value, '');
    assert.equal(duplicate.querySelector('#LOG_PWD').value, '');
    assert.equal(p.clicks, 0);
    assert.equal(duplicateClicks, 0);
    p.dom.window.close();
});

test('accepts a login button with different presentation classes but the same identity and handler', () => {
    const p = page();
    p.button.className = 'certificate-login-button revised-theme';
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    assert.equal(p.submittedPassword, 'Test123!');
    p.dom.window.close();
});

test('ignores hidden template copies of IDs within the active login form', () => {
    for (const selector of ['#LOG_PWD', '#LOGIN_BOX_BTN', '#CUSTOMERID', '#USERID']) {
        const p = page();
        const original = p.field.form.querySelector(selector);
        const duplicate = original.cloneNode(true);
        duplicate.style.display = 'none';
        original.after(duplicate);
        assert.equal(p.inspect().status, 'ready', selector);
        assert.equal(p.submit().status, 'submitted', selector);
        assert.equal(p.clicks, 1, selector);
        p.dom.window.close();
    }
});

test('rejects controls explicitly associated with a different form', () => {
    for (const selector of ['#LOG_PWD', '#LOGIN_BOX_BTN', '#CUSTOMERID', '#USERID']) {
        const p = page();
        p.field.form.insertAdjacentHTML('afterend', '<form id="other-form" method="post" action="/NCCB/NECV6B2BMainPlat"></form>');
        p.field.form.querySelector(selector).setAttribute('form', 'other-form');
        assert.equal(p.inspect().status, 'unsupported', selector);
        assert.notEqual(p.submit().status, 'submitted', selector);
        assert.equal(p.field.value, '', selector);
        assert.equal(p.clicks, 0, selector);
        p.dom.window.close();
    }
});

test('consumes the attempt before input handlers reenter or a click handler clears the password', () => {
    const p = page();
    assert.equal(p.inspect().status, 'ready');
    const nested = [];
    p.field.addEventListener('input', () => { nested.push(p.submit().status); });
    p.button.addEventListener('click', () => { p.field.value = ''; });
    assert.equal(p.submit().status, 'submitted');
    assert.ok(nested.length > 0);
    assert.ok(nested.every(status => status !== 'submitted'));
    assert.equal(p.field.value, '');
    assert.notEqual(p.inspect().status, 'ready');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    p.dom.window.close();
});

function framedPage() {
    const topUrl = 'https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp';
    const dom = new JSDOM('<body></body>', { url: topUrl, runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    supplyLayout(w);
    const run = w.eval(`(${script})`);
    const request = { origin: new URL(topUrl).origin, path: new URL(topUrl).pathname, ticket: 'synthetic-frame-ticket' };
    const addFrame = (options = {}) => {
        const parent = options.parent || w.document;
        const frame = parent.createElement('iframe');
        frame.src = options.url || 'https://b2b3.ccb.com/NCCB/V6/b2bmainV7.jsp';
        if (options.hidden) frame.style.display = 'none';
        parent.body.append(frame);
        const child = frame.contentWindow;
        supplyLayout(child);
        child.document.open();
        child.document.write(options.empty ? '<body></body>' : fixture);
        child.document.close();
        const field = child.document.querySelector('#LOG_PWD');
        const button = child.document.querySelector('#LOGIN_BOX_BTN');
        let clicks = 0;
        let submittedPassword;
        if (field) {
            field.addEventListener('input', () => { button.disabled = !field.value; });
            button.addEventListener('click', () => { clicks++; submittedPassword = field.value; });
        }
        return { frame, child, field, button, get clicks() { return clicks; },
            get submittedPassword() { return submittedPassword; } };
    };
    return { dom, w, addFrame, inspect: () => run({ ...request, operation: 'inspect' }),
        submit: () => run({ ...request, operation: 'submit', pin: 'Test123!' }) };
}

test('fills and clicks once in a visible same-origin child frame', () => {
    const p = framedPage();
    const c = p.addFrame();
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.submittedPassword, 'Test123!');
    assert.equal(c.clicks, 1);
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(c.clicks, 1);
    p.dom.window.close();
});

test('finds the certificate form inside nested visible same-origin frames', () => {
    const p = framedPage();
    const outer = p.addFrame({ empty: true });
    const c = p.addFrame({ parent: outer.child.document });
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.clicks, 1);
    p.dom.window.close();
});

test('fills the nested certificate form on the bank page with duplicate form IDs', () => {
    const p = framedPage();
    const outer = p.addFrame({ empty: true });
    const c = p.addFrame({ parent: outer.child.document,
        url: 'https://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09' });
    c.field.form.action = '/NCCB/NECV6B2BMainPlat';
    c.field.form.insertAdjacentHTML('afterend', '<form id="form_id" name="jhform2" method="post" action="/NCCB/NECV6B2BMainPlat"><h2>手机号/用户名登录</h2></form>');
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.submittedPassword, 'Test123!');
    assert.equal(c.clicks, 1);
    p.dom.window.close();
});

test('skips cross-origin frames even when they contain matching login controls', () => {
    const p = framedPage();
    const foreign = p.addFrame({ url: 'https://evil.example/NCCB/V6/b2bmainV7.jsp' });
    const c = p.addFrame();
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.clicks, 1);
    assert.equal(foreign.field.value, '');
    assert.equal(foreign.clicks, 0);
    p.dom.window.close();
});

test('refuses multiple visible matching certificate forms across frames', () => {
    const p = framedPage();
    const first = p.addFrame();
    const second = p.addFrame();
    assert.notEqual(p.inspect().status, 'ready');
    assert.notEqual(p.submit().status, 'submitted');
    for (const c of [first, second]) {
        assert.equal(c.field.value, '');
        assert.equal(c.clicks, 0);
    }
    p.dom.window.close();
});

test('refuses stale readiness when the child frame document is replaced', () => {
    const p = framedPage();
    const original = p.addFrame();
    assert.equal(p.inspect().status, 'ready');
    original.frame.remove();
    const replacement = p.addFrame();
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(replacement.field.value, '');
    assert.equal(replacement.clicks, 0);
    p.dom.window.close();
});

test('skips matching forms inside hidden frames', () => {
    const p = framedPage();
    const hidden = p.addFrame({ hidden: true });
    const c = p.addFrame();
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.clicks, 1);
    assert.equal(hidden.field.value, '');
    assert.equal(hidden.clicks, 0);
    p.dom.window.close();
});

test('does not fill or submit a frame hidden after readiness was checked', () => {
    const p = framedPage();
    const c = p.addFrame();
    assert.equal(p.inspect().status, 'ready');
    c.frame.style.display = 'none';
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(c.field.value, '');
    assert.equal(c.clicks, 0);
    p.dom.window.close();
});
test('wrong origin, protocol, port, path, or transaction never accepts the credential', () => {
    for (const bad of [url.replace('b2bbank.ccb.com', 'evil.example'), url.replace('https:', 'http:'),
        url.replace('.com/', '.com:444/'), url.replace('MNECV6B2BMainPlat_UN', 'other'),
        url.replace('N7N010', 'TRANSFER'), url.replace('LOGIN_TYPE=UC', 'LOGIN_TYPE=UM')]) {
        const p = page({ url: bad });
        assert.equal(p.inspect().status, 'unsupported', bad);
        assert.notEqual(p.submit().status, 'submitted');
        assert.equal(p.field.value, '');
        assert.equal(p.clicks, 0);
    }
});
test('waits for populated account and visible certificate form', () => {
    const p = page();
    p.w.document.querySelector('#CUSTOMERID').value = '';
    assert.equal(p.inspect().status, 'waiting');
    p.w.document.querySelector('#CUSTOMERID').value = 'TEST-CUSTOMER';
    p.w.document.querySelector('#Wrap_searchbar').style.display = 'none';
    assert.equal(p.inspect().status, 'waiting');
    p.w.document.querySelector('#Wrap_searchbar').style.display = '';
    assert.equal(p.inspect().status, 'ready');
});
test('does not overwrite user input or guess among duplicate password fields', () => {
    const p = page();
    p.field.value = 'user-entry';
    assert.equal(p.inspect().status, 'unsupported');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.field.value, 'user-entry');
    p.field.value = '';
    p.field.after(p.field.cloneNode());
    assert.equal(p.inspect().status, 'unsupported');
});
test('changed document, account, or replaced form rejects stale readiness', () => {
    for (const kind of ['document', 'account', 'form']) {
        const p = page();
        assert.equal(p.inspect().status, 'ready');
        if (kind === 'document') p.request.ticket = 'different-ticket';
        if (kind === 'account') p.w.document.querySelector('#USERID').value = 'OTHER';
        if (kind === 'form') p.w.document.querySelector('#form_id').outerHTML = fixture;
        assert.notEqual(p.submit().status, 'submitted', kind);
        assert.equal(p.w.document.querySelector('#LOG_PWD').value, '');
        assert.equal(p.clicks, 0);
    }
});
test('never forces a disabled button, and clears its unsubmitted password', () => {
    const p = page({ enable: false });
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'failed');
    assert.equal(p.button.disabled, true);
    assert.equal(p.clicks, 0);
    assert.equal(p.field.value, '');
});
test('rejects an unexpected form action before reading or writing credentials', () => {
    const p = page();
    p.w.document.querySelector('#form_id').action = 'https://evil.example/';
    assert.equal(p.inspect().status, 'unsupported');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.field.value, '');
});
test('does not submit after an input handler switches accounts', () => {
    const p = page();
    assert.equal(p.inspect().status, 'ready');
    p.field.addEventListener('input', () => { p.w.document.querySelector('#USERID').value = 'OTHER'; });
    assert.equal(p.submit().status, 'failed');
    assert.equal(p.clicks, 0);
    assert.equal(p.field.value, '');
});
test('recognizes a certificate form by the exact visible password label without guessing element IDs', () => {
    const p = page();
    p.w.document.querySelector('#form_id').id = 'certificate-form';
    p.w.document.querySelector('form').removeAttribute('name');
    p.field.id = 'bank-password';
    p.field.name = 'bank-password';
    p.field.placeholder = '登录密码:';
    p.button.id = 'bank-login';
    p.button.className = '';
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
});
test('semantic recognition tolerates changed headings and extra informational fields', () => {
    for (const kind of ['heading', 'extra-field']) {
        const p = page();
        p.field.id = 'bank-password';
        p.field.name = 'bank-password';
        if (kind === 'heading') p.w.document.querySelector('h2').textContent = '手机号/用户名登录';
        else p.field.insertAdjacentHTML('beforebegin', '<input type="text" value="unexpected">');
        assert.equal(p.inspect().status, 'ready');
        assert.equal(p.submit().status, 'submitted');
        assert.equal(p.clicks, 1);
    }
});
test('diagnostics report control structure without credential or account values', () => {
    const p = page();
    p.field.value = 'SECRET-SENTINEL';
    p.w.document.querySelector('#CUSTOMERID').value = 'ACCOUNT-SENTINEL';
    p.w.document.querySelector('#USERID').value = 'OPERATOR-SENTINEL';
    const diagnostic = p.run({...p.request, operation:'diagnose'});
    assert.equal(diagnostic.status, 'diagnostic');
    assert.ok(diagnostic.controls.some(e => e.id === 'LOG_PWD' && e.type === 'password'));
    const json = JSON.stringify(diagnostic);
    for (const secret of ['SECRET-SENTINEL','ACCOUNT-SENTINEL','OPERATOR-SENTINEL']) assert.ok(!json.includes(secret));
    assert.equal(p.clicks, 0);
    assert.equal(p.field.value, 'SECRET-SENTINEL');
});

test('supports a password-only login form after certificate authentication', () => {
    const p = page({ enable: false });
    p.field.form.querySelector('h2').remove();
    p.w.document.querySelector('#CUSTOMERID').remove();
    p.w.document.querySelector('#USERID').remove();
    p.field.form.removeAttribute('id');
    p.field.form.removeAttribute('name');
    p.field.id = 'new-driver-login-password';
    p.field.name = 'new-driver-login-password';
    p.button.id = 'login-action';
    p.field.addEventListener('input', () => { p.button.disabled = !p.field.value; });
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    assert.equal(p.submittedPassword, 'Test123!');
    p.dom.window.close();
});

test('hidden account fields do not block an otherwise ready login', () => {
    const p = page();
    p.w.document.querySelector('#CUSTOMERID').style.display = 'none';
    p.w.document.querySelector('#USERID').type = 'hidden';
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    p.dom.window.close();
});

test('recognizes associated login-password label when placeholder and IDs change', () => {
    const p = page();
    p.field.id = 'new-password';
    p.field.name = 'new-password';
    p.field.placeholder = '';
    p.field.insertAdjacentHTML('beforebegin', '<label for="new-password">登录密码：</label>');
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    p.dom.window.close();
});

test('waits for an inactive password control to become editable', () => {
    const p = page();
    p.field.readOnly = true;
    assert.equal(p.inspect().status, 'waiting');
    p.field.readOnly = false;
    assert.equal(p.inspect().status, 'ready');
    p.dom.window.close();
});

test('does not confuse new-password or payment-password fields with login', () => {
    for (const label of ['新密码', '确认密码', '支付密码', '交易密码']) {
        const p = page();
        p.field.id = 'unrelated-password';
        p.field.name = 'unrelated-password';
        p.field.placeholder = label;
        assert.notEqual(p.inspect().status, 'ready');
        assert.notEqual(p.submit().status, 'submitted');
        assert.equal(p.field.value, '');
        assert.equal(p.clicks, 0);
        p.dom.window.close();
    }
});

test('tracks renamed account fields even without machine-readable labels', () => {
    for (const duringInput of [false, true]) {
        const p = page({ enable: false });
        const customer = p.w.document.querySelector('#CUSTOMERID');
        const user = p.w.document.querySelector('#USERID');
        customer.id = customer.name = 'bank-customer';
        user.id = user.name = 'bank-user';
        p.field.addEventListener('input', () => {
            p.button.disabled = !p.field.value;
            if (duringInput) user.value = 'CHANGED-OPERATOR';
        });
        assert.equal(p.inspect().status, 'ready');
        if (!duringInput) user.value = 'CHANGED-OPERATOR';
        assert.notEqual(p.submit().status, 'submitted');
        assert.equal(p.clicks, 0);
        assert.equal(p.field.value, '');
        p.dom.window.close();
    }
});

test('an empty optional informational text field does not block login', () => {
    const p = page();
    p.field.insertAdjacentHTML('beforebegin', '<input name="optional-info" type="text" value="">');
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(p.clicks, 1);
    p.dom.window.close();
});

test('prefers the actual login button over same-text links in the live nested bank layout', () => {
    const p = framedPage();
    const outer = p.addFrame({ empty: true });
    const c = p.addFrame({ parent: outer.child.document, url: 'https://b2b3.ccb.com/NCCB/NECV6PubQuery' });
    c.field.form.action = '/NCCB/NECV6B2BMainPlat';
    c.field.form.insertAdjacentHTML('beforeend', '<a href="#help">登录</a><a href="#other">登 录</a>');
    let linkClicks = 0;
    for (const link of c.field.form.querySelectorAll('a')) link.addEventListener('click', () => linkClicks++);
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(c.clicks, 1);
    assert.equal(linkClicks, 0);
    assert.equal(c.submittedPassword, 'Test123!');
    p.dom.window.close();
});

test('still rejects multiple actual login buttons instead of choosing by order', () => {
    const p = page();
    p.button.insertAdjacentHTML('afterend', '<button type="button">登录</button>');
    assert.equal(p.inspect().status, 'unsupported');
    assert.notEqual(p.submit().status, 'submitted');
    assert.equal(p.clicks, 0);
    assert.equal(p.field.value, '');
    p.dom.window.close();
});

test('can use a unique login link when the template has no native button', () => {
    const p = page({ enable: false });
    p.button.remove();
    p.field.insertAdjacentHTML('afterend', '<a href="#login">登录</a>');
    let linkClicks = 0;
    const link = p.field.form.querySelector('a');
    link.addEventListener('click', event => { event.preventDefault(); linkClicks++; });
    assert.equal(p.inspect().status, 'ready');
    assert.equal(p.submit().status, 'submitted');
    assert.equal(linkClicks, 1);
    p.dom.window.close();
});
