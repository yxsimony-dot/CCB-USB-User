const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(path.join(__dirname,
    '../../src/UsbWebLauncher.App/Browser/PsbcInputTrace.js'), 'utf8');

function page(t, url = 'https://corpebank.psbc.com/#/login') {
    const dom = new JSDOM('<input id="account"><div id="ukeyLogin_div" tabindex="0" isbusy="0">' +
        '<div id="ukeyLogin_input"></div></div><button>login</button>',
    { url, runScripts: 'outside-only', pretendToBeVisual: true });
    t.after(() => dom.window.close());
    const w = dom.window;
    const field = w.document.getElementById('ukeyLogin_div');
    const mask = w.document.getElementById('ukeyLogin_input');
    w.g_input_connectFlag = 1;
    w.g_stopSendRequest = 1;
    const run = w.eval(`(${script})`);
    const drain = () => JSON.parse(JSON.stringify(run('drain')));
    return { w, field, mask, run, drain };
}

test('observes the original field without focusing, clicking or changing DOM', t => {
    const p = page(t);
    let sideEffects = 0;
    p.field.addEventListener('focus', () => sideEffects++);
    p.w.document.querySelector('button').addEventListener('click', () => sideEffects++);
    const before = p.w.document.body.innerHTML;
    const result = p.drain();
    assert.equal(result[0].maskLength, 0);
    assert.equal(result[0].connected, 1);
    assert.equal(result[0].busy, 0);
    assert.equal(p.w.document.body.innerHTML, before);
    assert.equal(sideEffects, 0);
    assert.deepEqual(p.drain(), []);
    p.mask.textContent = '\u25cf'.repeat(7);
    assert.equal(p.drain()[0].maskLength, 7);
});

test('never reads event characters, clipboard, input values or accounts', t => {
    const p = page(t);
    p.drain();
    const forbidden = () => { throw new Error('secret access'); };
    Object.defineProperty(p.w.document.getElementById('account'), 'value', { get: forbidden });
    Object.defineProperty(p.field, 'value', { get: forbidden });
    for (const type of ['keydown', 'keyup', 'beforeinput', 'input', 'paste']) {
        const event = new p.w.Event(type, { bubbles: true, cancelable: true });
        for (const name of ['key', 'code', 'keyCode', 'which', 'data', 'clipboardData'])
            Object.defineProperty(event, name, { get: forbidden });
        assert.equal(p.field.dispatchEvent(event), true);
    }
    const samples = p.drain();
    assert.deepEqual(samples.map(x => x.eventType), ['keydown', 'keyup', 'beforeinput', 'input', 'paste']);
    assert.deepEqual(Object.keys(samples[0]).sort(), ['eventType', 'elapsedMs', 'maskLength',
        'documentFocused', 'fieldFocused', 'trusted', 'connected', 'stopped', 'busy'].sort());
});

test('ignores events outside the secure field and never exports unexpected mask text', t => {
    const p = page(t);
    p.drain();
    p.w.document.getElementById('account').dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
    assert.deepEqual(p.drain(), []);
    p.mask.textContent = 'SYNTHETIC-SECRET';
    const result = p.drain();
    assert.equal(result[0].maskLength, -1);
    assert.doesNotMatch(JSON.stringify(result), /SYNTHETIC/);
});

test('uses passive listeners and attaches once across repeated polling', t => {
    const p = page(t);
    const original = p.w.document.addEventListener.bind(p.w.document);
    const options = [];
    p.w.document.addEventListener = (type, listener, option) => {
        options.push(option);
        original(type, listener, option);
    };
    p.drain();
    p.drain();
    assert.equal(options.length, 16);
    assert.ok(options.every(x => x.capture === true && x.passive === true));
    p.field.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
    assert.equal(p.drain().length, 1);
});

test('bounds queued events and stops all observation explicitly', t => {
    const p = page(t);
    p.drain();
    for (let i = 0; i < 1000; i++) p.field.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
    assert.equal(p.drain().length, 128);
    p.run('stop');
    p.mask.textContent = '*';
    p.field.dispatchEvent(new p.w.Event('keyup', { bubbles: true }));
    assert.deepEqual(p.drain(), []);
});

test('limits the total number of observations', t => {
    const p = page(t);
    let total = p.drain().length;
    for (let batch = 0; batch < 20; batch++) {
        for (let i = 0; i < 100; i++) p.field.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
        total += p.drain().length;
    }
    assert.equal(total, 1024);
});

test('stops after ten minutes without needing another host poll', t => {
    const p = page(t);
    let expire;
    p.w.setTimeout = callback => { expire = callback; return 1; };
    p.drain();
    expire();
    p.field.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
    assert.deepEqual(p.drain(), []);
});

test('tracks the current secure field after the bank replaces its DOM', t => {
    const p = page(t);
    p.drain();
    const next = p.field.cloneNode(true);
    p.field.replaceWith(next);
    next.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
    assert.equal(p.drain()[0].eventType, 'keydown');
});

test('stops on SPA route departure and pagehide', t => {
    for (const type of ['hashchange', 'pagehide']) {
        const p = page(t);
        p.drain();
        if (type === 'hashchange') p.w.location.hash = '/index';
        p.w.dispatchEvent(new p.w.Event(type));
        p.w.location.hash = '/login';
        p.field.dispatchEvent(new p.w.Event('keydown', { bubbles: true }));
        assert.deepEqual(p.drain(), []);
    }
});

for (const url of [
    'http://corpebank.psbc.com/#/login', 'https://corpebank.psbc.com:444/#/login',
    'https://user@corpebank.psbc.com/#/login', 'https://corpebank.psbc.com/other#/login',
    'https://corpebank.psbc.com/?data=secret#/login', 'https://corpebank.psbc.com/#/index',
    'https://corpebank.psbc.com/#/login?tab=userLogin', 'https://corpebank.psbc.com.evil.test/#/login'
]) {
    test(`rejects address ${url}`, t => {
        const p = page(t, url);
        assert.deepEqual(p.drain(), []);
        assert.equal(p.w.__usbLauncherPsbcInputTrace, undefined);
    });
}

test('rejects child frames even on the allowed bank origin', t => {
    const p = page(t);
    const frame = p.w.document.createElement('iframe');
    frame.src = 'https://corpebank.psbc.com/#/login';
    p.w.document.body.append(frame);
    const run = frame.contentWindow.eval(`(${script})`);
    assert.equal(run('drain').length, 0);
    assert.equal(frame.contentWindow.__usbLauncherPsbcInputTrace, undefined);
});
