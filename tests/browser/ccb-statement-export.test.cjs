const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const scriptPath = path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbStatementExport.js');
const source = fs.readFileSync(scriptPath, 'utf8');
const origin = 'https://b2b3.ccb.com';
const endpoint = '/NCCB/NECV6B2BMainPlat_09_LH';
const home = '<a id="entry" onclick="taskBar.createWin(\'existing-bank-argument\')">活期账户</a><iframe></iframe>';
const accounts = `<form name="jhform" action="${endpoint}"><input name="all_0" type="checkbox" checked><input name="all_1" type="checkbox" checked><input name="ACC_NO" type="checkbox" value="PRIVATE_FIRST"><input name="ACC_NO" type="checkbox" value="PRIVATE_SECOND" checked><input name="Submit3" type="button" onclick="detailQuery()"></form>`;
const query = `<form name="jhform" action="${endpoint}" onsubmit="return go()"><input id="StDt" name="StDt"><input id="EdDt" name="EdDt"><input name="ACC_NO" type="hidden" value="PRIVATE_FIRST"><input name="Submit4" type="submit"></form>`;
const resultIdentity = '<input name="ACC_NO" type="hidden" value="PRIVATE_FIRST"><input name="StDt" type="hidden" value="20260201"><input name="EdDt" type="hidden" value="20260228">';
const resultBase = `<form name="jhform" action="${endpoint}"><input name="SEQ_NO" type="hidden" value="PRIVATE_SEQUENCE">${resultIdentity}</form><form name="elecPrintForm" action="${endpoint}"></form>`;
const pdf = '<li onclick="submitSelect(\'original-pdf-argument\')">PDF下载</li>';
// Matches the bank factory's body-appended menus and closure-bound hover behavior.
function factoryMenus(t) {
    const p = page(t); p.queried();
    p.set(resultBase + '<nav><a id="current">下载当前页</a><a id="dlall" class="tableRBtn download">下载全部</a><a id="currentBottom">下载当前页</a><a id="dlAll" class="tableRBtn download">下载全部</a></nav>');
    const boxes = [], clicks = [0, 0, 0, 0], timers = [], shows = [];
    const owners = [...p.child.querySelectorAll('nav a')];
    for (const [index, owner] of owners.entries()) {
        const box = p.child.createElement('div'); box.className = 'downloadBox'; box.style.display = 'none';
        box.innerHTML = '<span><a class="tableLBtn underline download">' + owner.textContent + '</a></span><ul>' + pdf + '</ul>';
        p.child.body.append(box); boxes.push(box);
        const show = () => { boxes.forEach(other => other.style.display = 'none'); box.style.display = 'block'; clearTimeout(timers[index]); };
        shows.push(show); owner.addEventListener('mouseover', show); box.addEventListener('mouseover', show);
        // jQuery's mouseleave special event is backed by native mouseout.
        box.addEventListener('mouseout', () => { timers[index] = setTimeout(() => { box.style.display = 'none'; }, 100); });
        box.querySelector('li').addEventListener('click', () => clicks[index]++);
    }
    t.after(() => timers.forEach(clearTimeout));
    return { ...p, boxes, owners, clicks, shows };
}
function page(t, html = home, url = origin + '/NCCB/V6/b2bindex.jsp') {
    const dom = new JSDOM(html, { url, runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    const visible = win => {
        Object.defineProperty(win.Element.prototype, 'getClientRects', { configurable: true, value() { return [{ width: 100, height: 20 }]; } });
        Object.defineProperty(win.document, 'readyState', { configurable: true, get: () => 'complete' });
    };
    visible(w);
    const child = w.document.querySelector('iframe')?.contentDocument;
    if (child) visible(child.defaultView);
    const run = (operation, extra = {}) => JSON.parse(JSON.stringify(w.eval(`(${source})`)({ operation, runId: 'run-one', origin, startDate: '20260201', endDate: '20260228', ...extra })));
    const set = html => { child.body.innerHTML = html; return child; };
    const countClicks = selector => { let n = 0; child.querySelector(selector).addEventListener('click', e => { e.preventDefault(); n++; }); return () => n; };
    const start = () => { run('open-accounts'); set(accounts); return run('select-first'); };
    const queried = () => { start(); set(query); countClicks('[name=Submit4]'); assert.equal(run('query').status, 'queried'); };
    return { w, child, run, set, start, queried, countClicks };
}

// Sanitized bank result shape: the selection value includes both identity fields.
function namingPage(t) {
    const p = page(t);
    const account = '1234567890120007', company = '示例物业服务有限公司清远分公司工会';
    const composite = account + '@' + company + '@人民币@156@0@0@1';
    const adapt = html => html.replaceAll('PRIVATE_FIRST', composite);
    p.run('open-accounts'); p.set(adapt(accounts)); p.run('select-first');
    p.set(adapt(query)); p.countClicks('[name=Submit4]'); p.run('query');
    p.set(adapt(resultBase).replace('</form>', `<table id="headTable" class="tableConfirm"><tr><td class="cfmTitle cfmSty1">账　号：</td><td class="cfmInfo cfmSty4 acc">${account}</td><td class="cfmTitle cfmSty1">账户名称：</td><td class="cfmInfo cfmSty4 fzWord">${company}</td></tr></table><input name="checkedAccno" type="hidden" value="${account}"><input name="checkedAccName" type="hidden" value="${company}"></form>`) + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>');
    return { ...p, account, company, clicks: p.countClicks('li') };
}

// The table schema (rsTable/tableList and two TD header rows of 16/2 cells)
// was verified offline in 21 cached nonempty bank results. This zero-row variant
// is a conservative derived fixture, not a captured empty-result document.
const emptyStatementTable = '<table id="rsTable" class="tableList"><tr class="tableListHead">' + '<td>表头</td>'.repeat(16) + '</tr><tr class="tableListHead"><td>借方</td><td>贷方</td></tr></table>';
function emptyStatementPage(t) {
    const p = namingPage(t);
    p.child.querySelectorAll('[name="SEQ_NO"]').forEach(node => node.remove());
    p.child.querySelector('form[name="jhform"]').insertAdjacentHTML('beforeend', emptyStatementTable);
    Object.defineProperty(p.child, 'readyState', { configurable: true, get: () => 'complete' });
    return p;
}

test('identifies a fresh complete strictly empty statement result without clicking download', t => {
    const p = emptyStatementPage(t);
    assert.deepEqual(p.run('inspect'), { status: 'ready', stage: 'results' });
    assert.deepEqual(p.run('identify'), { status: 'empty', stage: 'results' });
    assert.equal(p.run('download').status, 'empty'); assert.equal(p.clicks(), 0);
});

test('waits while a zero-row result candidate is parsing and recognizes empty only when complete', t => {
    const p = emptyStatementPage(t);
    let readyState = 'loading';
    Object.defineProperty(p.child, 'readyState', { configurable: true, get: () => readyState });
    for (const phase of ['loading', 'interactive']) {
        readyState = phase;
        assert.deepEqual(p.run('identify'), { status: 'waiting', stage: 'results' });
        assert.deepEqual(p.run('download'), { status: 'waiting', stage: 'results' });
        assert.equal(p.clicks(), 0);
    }
    readyState = 'complete';
    assert.deepEqual(p.run('identify'), { status: 'empty', stage: 'results' });
    assert.equal(p.clicks(), 0);
});

test('never calls missing or malformed result content an empty statement', t => {
    for (const mutate of [
        p => p.child.querySelector('#rsTable').remove(),
        p => p.child.querySelector('#rsTable').after(p.child.querySelector('#rsTable').cloneNode(true)),
        p => p.child.querySelector('#rsTable').insertRow().insertCell().textContent = '页面错误',
        p => p.child.querySelector('#rsTable tr').deleteCell(0),
        p => p.child.querySelector('#rsTable').style.display = 'none',
        p => p.child.querySelector('#rsTable tr').style.display = 'none',
        p => p.child.querySelector('#headTable').remove(),
        p => p.child.querySelector('form[name="elecPrintForm"]').remove(),
        p => p.child.querySelector('form[name="jhform"]').insertAdjacentHTML('beforeend', '<input name="SEQ_NO" type="checkbox">'),
        p => p.child.querySelector('form[name="jhform"]').insertAdjacentHTML('beforeend', '<input name="SEQ_NO" type="hidden">'),
        p => p.child.body.insertAdjacentHTML('beforeend', '<input type="password" id="LOG_PWD">')
    ]) {
        const p = emptyStatementPage(t); mutate(p);
        assert.notEqual(p.run('identify').status, 'empty'); assert.equal(p.clicks(), 0);
    }
});

test('empty statement requires completed new result and matching month account and naming', t => {
    for (const mutate of [
        p => Object.defineProperty(p.child, 'readyState', { get: () => 'loading' }),
        p => { p.child.querySelector('[name="StDt"]').value = '20260101'; },
        p => { p.child.querySelector('[name="ACC_NO"]').value = 'another-account'; },
        p => { p.child.querySelector('[name="checkedAccName"]').value = '另一公司'; },
        p => p.w.document.querySelector('iframe').remove()
    ]) {
        const p = emptyStatementPage(t); mutate(p);
        assert.notEqual(p.run('identify').status, 'empty'); assert.equal(p.clicks(), 0);
    }
});

test('in-place rewriting of the submitted query form is not proof of a new empty result', t => {
    const p = emptyStatementPage(t);
    const current = p.child.querySelector('form[name="jhform"]');
    // A controlled same-object query form with the same result-like children:
    // submit first through the adapter, then mutate that very form into zero rows.
    const composite = p.account + '@' + p.company + '@人民币@156@0@0@1';
    const original = page(t); original.run('open-accounts'); original.set(accounts.replaceAll('PRIVATE_FIRST', composite)); original.run('select-first');
    original.set(query.replaceAll('PRIVATE_FIRST', composite)); original.countClicks('[name="Submit4"]');
    const submitted = original.child.querySelector('form'); original.run('query');
    submitted.innerHTML = current.innerHTML;
    original.child.body.insertAdjacentHTML('beforeend', `<form name="elecPrintForm" action="${endpoint}"></form>`);
    Object.defineProperty(original.child, 'readyState', { configurable: true, get: () => 'complete' });
    assert.notEqual(original.run('identify').status, 'empty');
});

test('disposing an empty statement run releases the retained query form reference', t => {
    const p = emptyStatementPage(t); assert.equal(p.run('identify').status, 'empty');
    p.run('dispose'); assert.equal(p.w.document.__usbWebLauncherStatementExportV1.queryForm, null);
});

test('identifies complete company including union suffix and preserves leading zero account suffix', t => {
    const p = namingPage(t);
    const identified = p.run('identify');
    assert.deepEqual(identified, { status: 'identified', stage: 'results', companyName: p.company, accountLast4: '0007' });
    assert.equal(JSON.stringify(identified).includes(p.account), false);
    assert.equal(p.run('download').status, 'downloading'); assert.equal(p.clicks(), 1);
});

test('naming rejects absent, duplicate, truncated or mismatched identity fields', t => {
    for (const mutate of [
        p => p.child.querySelector('#headTable').remove(),
        p => p.child.querySelector('#headTable').after(p.child.querySelector('#headTable').cloneNode(true)),
        p => p.child.querySelector('[name=checkedAccName]').remove(),
        p => p.child.querySelector('[name=checkedAccno]').after(p.child.querySelector('[name=checkedAccno]').cloneNode(true)),
        p => { p.child.querySelector('[name=checkedAccno]').value = '1234567899990007'; },
        p => { p.child.querySelector('[name=checkedAccName]').value = '另一家公司'; },
        p => { p.child.querySelector('#headTable .fzWord').textContent = '示例物业…'; },
        p => { p.child.querySelector('#headTable .acc').textContent = '123***0007'; },
        p => { p.child.querySelector('#headTable .fzWord').textContent = ''; },
        p => { p.child.querySelector('#headTable .fzWord').style.display = 'none'; }
    ]) {
        const p = namingPage(t); mutate(p);
        assert.equal(p.run('identify').code, 'naming-identity'); assert.equal(p.clicks(), 0);
    }
});

test('naming requires matching selected composite identity and result month', t => {
    const p = namingPage(t);
    p.child.querySelector('#headTable .fzWord').textContent = '其他公司工会';
    p.child.querySelector('[name=checkedAccName]').value = '其他公司工会';
    assert.equal(p.run('identify').code, 'naming-identity');
    const q = namingPage(t); q.child.querySelector('[name=StDt]').value = '20260101';
    assert.equal(q.run('identify').code, 'result-identity');
});

test('naming rejects truncated or control characters even when all source fields agree', t => {
    for (const company of ['示例公司…', '示例公司...', '示例\u0001公司']) {
        const p = namingPage(t);
        const composite = p.w.document.__usbWebLauncherStatementExportV1.account.replace(p.company, company);
        p.w.document.__usbWebLauncherStatementExportV1.account = composite;
        p.child.querySelector('[name=ACC_NO]').value = composite;
        p.child.querySelector('[name=checkedAccName]').value = company;
        p.child.querySelector('#headTable .fzWord').textContent = company;
        assert.equal(p.run('identify').code, 'naming-identity');
    }
});

test('identification binds filename metadata to this run and cannot be replaced on retry', t => {
    const p = namingPage(t); assert.equal(p.run('identify').status, 'identified');
    p.child.querySelector('#headTable .fzWord').textContent = '其他公司';
    assert.equal(p.run('identify').code, 'naming-identity');
});

test('naming preserves a full 225-character company and rejects 226 without truncating', t => {
    for (const length of [225, 226]) {
        const p = namingPage(t), company = '甲'.repeat(length);
        const state = p.w.document.__usbWebLauncherStatementExportV1;
        state.account = state.account.replace(p.company, company);
        p.child.querySelector('[name=ACC_NO]').value = state.account;
        p.child.querySelector('[name=checkedAccName]').value = company;
        p.child.querySelector('#headTable .fzWord').textContent = company;
        const reply = p.run('identify');
        if (length === 225) { assert.equal(reply.status, 'identified'); assert.equal(reply.companyName, company); }
        else assert.equal(reply.code, 'naming-identity');
    }
});

test('download rechecks previously identified filename identity including bank event changes', t => {
    for (const onClick of [false, true]) {
        const p = namingPage(t); assert.equal(p.run('identify').status, 'identified');
        const mutate = () => { p.child.querySelector('#headTable .fzWord').textContent = '其他公司'; };
        if (onClick) {
            p.child.querySelector('ul').style.display = 'none';
            p.child.querySelector('#dlall').addEventListener('click', mutate);
            assert.equal(p.run('download').status, 'waiting');
            p.child.querySelector('ul').style.display = 'block';
        } else mutate();
        assert.equal(p.run('download').code, 'naming-identity'); assert.equal(p.clicks(), 0);
    }
});

test('dispose clears the confirmed naming metadata', t => {
    const p = namingPage(t); assert.equal(p.run('identify').status, 'identified'); p.run('dispose');
    assert.equal(p.w.document.__usbWebLauncherStatementExportV1.naming, null);
    assert.equal(p.run('identify').status, 'failed');
});
test('opens original main entry exactly once even with an existing results tab', t => {
    const p = page(t); p.set(resultBase + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>');
    let clicks = 0; p.w.document.querySelector('#entry').addEventListener('click', () => clicks++);
    assert.equal(p.run('open-accounts').status, 'opened');
    assert.equal(p.run('open-accounts').status, 'waiting');
    assert.equal(clicks, 1);
});
test('body-appended ccbDownload hover reveals only its original all-PDF menu', t => {
    const p = factoryMenus(t);
    assert.equal(p.run('download').status, 'downloading');
    assert.deepEqual(p.clicks, [0, 1, 0, 0]);
    assert.equal(p.run('download').status, 'waiting'); assert.deepEqual(p.clicks, [0, 1, 0, 0]);
});
test('ccbDownload resets a preopened current-page menu before proving all-menu ownership', async t => {
    const p = factoryMenus(t); p.shows[0]();
    let hovers = 0; p.owners[1].addEventListener('mouseover', () => hovers++);
    assert.equal(p.run('download').status, 'waiting'); assert.equal(hovers, 0);
    assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(p.run('download').status, 'downloading'); assert.equal(hovers, 1);
    assert.deepEqual(p.clicks, [0, 1, 0, 0]);
});
test('ccbDownload refuses an unwired owner or an owner revealing a current-page menu', t => {
    for (const wrong of [false, true]) {
        const p = factoryMenus(t); p.owners[1].removeEventListener('mouseover', p.shows[1]);
        if (wrong) p.owners[1].addEventListener('mouseover', p.shows[0]);
        assert.equal(p.run('download').status, 'unsupported'); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    }
});
test('ccbDownload rejects multiple revealed menus and duplicate PDF items', t => {
    for (const duplicatePdf of [false, true]) {
        const p = factoryMenus(t);
        if (duplicatePdf) p.boxes[1].querySelector('ul').insertAdjacentHTML('beforeend', pdf);
        else p.owners[1].addEventListener('mouseover', () => { p.boxes[3].style.display = 'block'; });
        assert.equal(p.run('download').status, 'ambiguous'); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    }
});
test('ccbDownload blocks reentry from hover and the original PDF click', t => {
    const p = factoryMenus(t); const nested = [];
    p.owners[1].addEventListener('mouseover', () => nested.push(p.run('download').status));
    p.boxes[1].querySelector('li').addEventListener('click', () => nested.push(p.run('download').status));
    assert.equal(p.run('download').status, 'downloading');
    assert.deepEqual(nested, ['waiting', 'waiting']); assert.deepEqual(p.clicks, [0, 1, 0, 0]);
});
test('ccbDownload revalidates account, dates, frame and disposal after hover events', t => {
    for (const mutate of [
        p => { p.child.querySelector('[name=ACC_NO]').value = 'OTHER'; },
        p => { p.child.querySelector('[name=StDt]').value = '20260101'; },
        p => p.w.document.querySelector('iframe').remove(),
        p => p.run('dispose')
    ]) {
        const p = factoryMenus(t); p.owners[1].addEventListener('mouseover', () => mutate(p));
        assert.equal(p.run('download').status, 'failed'); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    }
});
test('ccbDownload keeps anchor identity across reset and refuses a stale visible menu', async t => {
    const p = factoryMenus(t); p.shows[0](); assert.equal(p.run('download').status, 'waiting');
    // An immediate retry cannot reuse the still-visible unrelated menu.
    assert.equal(p.run('download').status, 'unsupported'); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    await new Promise(resolve => setTimeout(resolve, 150));
    p.owners[1].replaceWith(p.owners[1].cloneNode(true));
    assert.equal(p.run('download').status, 'failed'); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
});
test('ccbDownload reset events cannot reenter or change identity before waiting', t => {
    for (const alter of [false, true]) {
        const p = factoryMenus(t); p.shows[0](); const nested = [];
        p.boxes[0].addEventListener('mouseout', () => {
            nested.push(p.run('download').status);
            if (alter) p.child.querySelector('[name=ACC_NO]').value = 'OTHER';
        });
        assert.equal(p.run('download').status, alter ? 'failed' : 'waiting');
        assert.deepEqual(nested, ['waiting']); assert.deepEqual(p.clicks, [0, 0, 0, 0]);
    }
});
test('selects first account only, clears select-all, invokes original detail handler once', t => {
    const p = page(t); p.run('open-accounts'); p.set(accounts); const count = p.countClicks('[name=Submit3]');
    assert.equal(p.run('inspect').stage, 'accounts');
    assert.equal(p.run('select-first').status, 'selected');
    assert.deepEqual([...p.child.querySelectorAll('input[type=checkbox]')].map(x => x.checked), [false, false, true, false]);
    assert.equal(p.run('select-first').status, 'waiting'); assert.equal(count(), 1);
});
test('query waits for the document to finish parsing before validating its account or submitting', t => {
    const p = page(t); p.start();
    p.set(query); p.child.querySelector('[name=ACC_NO]').remove();
    const clicks = p.countClicks('[name=Submit4]');
    let readyState = 'loading';
    Object.defineProperty(p.child, 'readyState', { configurable: true, get: () => readyState });
    for (const phase of ['loading', 'interactive']) {
        readyState = phase;
        assert.deepEqual(p.run('inspect'), { status: 'waiting', stage: 'query' });
        assert.deepEqual(p.run('query'), { status: 'waiting', stage: 'query' });
        assert.equal(p.child.querySelector('[name=StDt]').value, '');
        assert.equal(clicks(), 0);
    }
    p.child.querySelector('form').insertAdjacentHTML('beforeend', '<input name="ACC_NO" type="hidden" value="PRIVATE_FIRST">');
    readyState = 'complete';
    assert.deepEqual(p.run('inspect'), { status: 'ready', stage: 'query' });
    assert.equal(p.run('query').status, 'queried');
    assert.equal(clicks(), 1);
});

test('completed query with missing or different account still fails without submission', t => {
    for (const absent of [true, false]) {
        const p = page(t); p.start(); p.set(query);
        const account = p.child.querySelector('[name=ACC_NO]');
        if (absent) account.remove(); else account.value = 'OTHER_ACCOUNT';
        const clicks = p.countClicks('[name=Submit4]');
        assert.equal(p.run('query').code, 'account-mismatch'); assert.equal(clicks(), 0);
    }
});

test('statement waits for complete account lists and nonempty results before choosing or downloading', t => {
    const p = page(t); p.run('open-accounts'); p.set(accounts);
    let readyState = 'loading';
    Object.defineProperty(p.child, 'readyState', { configurable: true, get: () => readyState });
    const selections = p.countClicks('[name=Submit3]');
    assert.deepEqual(p.run('inspect'), { status: 'waiting', stage: 'accounts' });
    assert.equal(p.run('select-first').status, 'waiting'); assert.equal(selections(), 0);
    readyState = 'complete'; assert.equal(p.run('select-first').status, 'selected'); assert.equal(selections(), 1);
    const q = namingPage(t); q.child.querySelector('[name=ACC_NO]').remove();
    Object.defineProperty(q.child, 'readyState', { configurable: true, get: () => 'interactive' });
    assert.deepEqual(q.run('inspect'), { status: 'waiting', stage: 'results' });
    assert.equal(q.run('identify').status, 'waiting'); assert.equal(q.run('download').status, 'waiting');
    assert.equal(q.clicks(), 0);
});

test('sets complete month without changing defaults and submits original query once', t => {
    const p = page(t); p.start(); p.set(query + '<input id="default" value="unchanged">'); const count = p.countClicks('[name=Submit4]');
    assert.equal(p.run('query').status, 'queried');
    assert.equal(p.child.querySelector('#StDt').value, '20260201'); assert.equal(p.child.querySelector('#EdDt').value, '20260228');
    assert.equal(p.child.querySelector('#default').value, 'unchanged');
    assert.equal(p.run('query').status, 'waiting'); assert.equal(count(), 1);
});
test('rejects account mismatch, unsupported form handlers and invalid dates before query', t => {
    for (const modify of [doc => doc.querySelector('[name=ACC_NO]').value = 'OTHER', doc => doc.querySelector('form').setAttribute('onsubmit', 'other()')]) {
        const p = page(t); p.start(); p.set(query); modify(p.child); const count = p.countClicks('[name=Submit4]');
        assert.ok(['unsupported', 'failed'].includes(p.run('query').status)); assert.equal(count(), 0);
    }
    const p = page(t); p.start(); p.set(query); const count = p.countClicks('[name=Submit4]');
    assert.equal(p.run('query', { endDate: '20260230' }).status, 'failed'); assert.equal(count(), 0);
});
test('download chooses download-all sibling PDF, never current-page PDF, with original arguments', t => {
    const p = page(t); p.queried(); p.set(resultBase + '<div><a id="current">下载当前页</a><ul>' + pdf + '</ul></div><div><a id="dlall">下载全部</a><ul>' + pdf + '</ul></div>');
    const current = p.countClicks('#current + ul li'), all = p.countClicks('#dlall + ul li');
    assert.equal(p.run('inspect').stage, 'results'); assert.equal(p.run('download').status, 'downloading');
    assert.equal(current(), 0); assert.equal(all(), 1); assert.equal(p.run('download').status, 'waiting'); assert.equal(all(), 1);
});
test('opens uniquely owned hidden menu once and waits before selecting PDF', t => {
    const p = page(t); p.queried(); p.set(resultBase + '<a id="dlAll" aria-controls="allMenu">下载全部</a><ul id="allMenu" style="display:none">' + pdf + '</ul>');
    const anchor = p.countClicks('#dlAll'), item = p.countClicks('li');
    assert.equal(p.run('download').status, 'waiting'); assert.equal(p.run('download').status, 'waiting'); assert.equal(anchor(), 1); assert.equal(item(), 0);
    p.child.querySelector('ul').style.display = 'block'; assert.equal(p.run('download').status, 'downloading'); assert.equal(item(), 1);
});
test('rejects ambiguous, shared or unowned PDF menus without clicking', t => {
    for (const html of [
        '<a id="dlall">下载全部</a><div></div><ul>' + pdf + '</ul>',
        '<a id="dlall">下载全部</a><ul>' + pdf + pdf + '</ul>',
        '<a id="dlall" aria-controls="menu">下载全部</a><a aria-controls="menu">下载当前页</a><ul id="menu">' + pdf + '</ul>'
    ]) {
        const p = page(t); p.queried(); p.set(resultBase + html); let clicks = 0; p.child.addEventListener('click', () => clicks++);
        assert.ok(['unsupported', 'ambiguous'].includes(p.run('download').status)); assert.equal(clicks, 0);
    }
});
test('requires this run to select/query and never emits account values or body text', t => {
    const p = page(t); p.set(query); const count = p.countClicks('[name=Submit4]');
    assert.equal(p.run('query').status, 'failed'); assert.equal(count(), 0);
    Object.defineProperty(p.w.document, 'cookie', { get() { throw Error('must not read cookies'); } });
    assert.doesNotMatch(JSON.stringify(p.run('inspect')), /PRIVATE|202602/);
    p.run('dispose'); assert.equal(p.run('query').status, 'failed');
});
test('rejects off-origin, nonstandard ports, wrong root paths and conflicting run IDs', t => {
    for (const url of ['http://b2b3.ccb.com/NCCB/V6/b2bindex.jsp', 'https://b2b3.ccb.com:444/NCCB/V6/b2bindex.jsp', 'https://evil.test/NCCB/V6/b2bindex.jsp', origin + '/NCCB/login']) {
        const p = page(t, home, url); assert.equal(p.run('open-accounts').status, 'unsupported');
    }
    const p = page(t); assert.equal(p.run('open-accounts').status, 'opened'); assert.equal(p.run('open-accounts', { runId: 'other' }).status, 'failed');
    assert.equal(p.run('inspect', { origin: 'https://xc.b2b3.ccb.com' }).status, 'unsupported');
});
test('ignores hidden frames and rejects multiple visible account documents', t => {
    const p = page(t); p.run('open-accounts'); p.set(accounts); p.w.document.querySelector('iframe').style.display = 'none';
    assert.notEqual(p.run('inspect').stage, 'accounts');
    p.w.document.querySelector('iframe').style.display = ''; p.w.document.body.insertAdjacentHTML('beforeend', '<iframe></iframe>');
    const second = p.w.document.querySelectorAll('iframe')[1].contentDocument;
    Object.defineProperty(second.defaultView.Element.prototype, 'getClientRects', { value() { return [{}]; } }); second.body.innerHTML = accounts;
    assert.equal(p.run('select-first').status, 'ambiguous');
});
test('does not navigate the old query while the newly requested account list is loading', t => {
    const p = page(t); p.set(query + '<input id="back" type="button" onclick="BacktoFirst()">');
    const count = p.countClicks('#back'); p.run('open-accounts');
    assert.equal(p.run('select-first').status, 'waiting'); assert.equal(p.run('select-first').status, 'waiting'); assert.equal(count(), 0);
});
test('expired mutations never click, including a zero or invalid expiry', t => {
    for (const expiresAt of [0, Date.now() - 1000, 'invalid']) {
        const p = page(t); let clicks = 0; p.w.document.querySelector('#entry').addEventListener('click', () => clicks++);
        assert.equal(p.run('open-accounts', { expiresAt }).status, 'failed'); assert.equal(clicks, 0);
    }
});
test('a disabled first account never causes selection of another account', t => {
    const p = page(t); p.run('open-accounts'); p.set(accounts); p.child.querySelector('[name=ACC_NO]').disabled = true;
    const count = p.countClicks('[name=Submit3]'); assert.equal(p.run('select-first').status, 'unsupported'); assert.equal(count(), 0);
});
test('document replacement during query change events prevents submission', t => {
    const p = page(t); p.start(); p.set(query); const count = p.countClicks('[name=Submit4]');
    p.child.querySelector('#StDt').addEventListener('change', () => p.w.document.querySelector('iframe').remove());
    assert.equal(p.run('query').status, 'failed'); assert.equal(count(), 0);
});
test('changing account identity in a date event prevents submission', t => {
    const p = page(t); p.start(); p.set(query); const count = p.countClicks('[name=Submit4]');
    p.child.querySelector('#StDt').addEventListener('change', () => p.child.querySelector('[name=ACC_NO]').value = 'OTHER');
    assert.equal(p.run('query').status, 'failed'); assert.equal(count(), 0);
});
test('accepts descendant and explicitly associated external PDF menus', t => {
    for (const html of ['<a id="dlall">下载全部<ul>' + pdf + '</ul></a>', '<a id="dlall" aria-controls="menu">下载全部</a><div></div><ul id="menu">' + pdf + '</ul>']) {
        const p = page(t); p.queried(); p.set(resultBase + html); const count = p.countClicks('li');
        assert.equal(p.run('download').status, 'downloading'); assert.equal(count(), 1);
    }
});
test('identifies results with multiple sequence rows', t => {
    const p = page(t); p.queried(); p.set(resultBase.replace('</form>', '<input name="SEQ_NO" type="checkbox"></form>') + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>');
    assert.equal(p.run('inspect').stage, 'results');
});
test('does not access declared cross-origin frames', t => {
    const p = page(t); const frame = p.w.document.querySelector('iframe'); frame.setAttribute('src', 'https://other.ccb.com' + endpoint);
    let touched = false; Object.defineProperty(frame, 'contentDocument', { get() { touched = true; throw Error('cross-origin'); } });
    assert.equal(p.run('inspect').stage, 'home'); assert.equal(touched, false);
});
test('rejects redirected frames outside allowed endpoint paths', t => {
    const p = page(t); const other = page(t, accounts, origin + '/NCCB/outside');
    Object.defineProperty(p.w.document.querySelector('iframe'), 'contentDocument', { get: () => other.w.document });
    assert.equal(p.run('inspect').stage, 'home');
});
test('deduplicates document references and limits traversal depth', t => {
    const p = page(t); p.set(accounts); p.w.document.body.insertAdjacentHTML('beforeend', '<iframe></iframe>');
    Object.defineProperty(p.w.document.querySelectorAll('iframe')[1], 'contentDocument', { get: () => p.child });
    assert.equal(p.run('inspect').stage, 'accounts');
    p.set('<iframe></iframe>'); let doc = p.child;
    for (let depth = 2; depth <= 5; depth++) {
        doc = doc.querySelector('iframe').contentDocument;
        Object.defineProperty(doc.defaultView.Element.prototype, 'getClientRects', { value() { return [{}]; } });
        doc.body.innerHTML = depth === 5 ? accounts : '<iframe></iframe>';
    }
    assert.equal(p.run('inspect').stage, 'home');
});
test('query input handler reentry cannot submit twice', t => {
    const p = page(t); p.start(); p.set(query); const count = p.countClicks('[name=Submit4]');
    p.child.querySelector('#StDt').addEventListener('input', () => p.run('query'), { once: true });
    assert.equal(p.run('query').status, 'queried'); assert.equal(count(), 1);
});
test('query controls assigned to another form cannot submit', t => {
    const p = page(t); p.start(); p.set(query + '<form id="other" action="https://outside.test/"></form>');
    p.child.querySelector('[name=Submit4]').setAttribute('form', 'other'); const count = p.countClicks('[name=Submit4]');
    assert.equal(p.run('query').status, 'unsupported'); assert.equal(count(), 0);
});
test('query event changing the form action cannot submit', t => {
    const p = page(t); p.start(); p.set(query); const count = p.countClicks('[name=Submit4]');
    p.child.querySelector('#StDt').addEventListener('change', () => p.child.querySelector('form').action = 'https://outside.test/');
    assert.equal(p.run('query').status, 'failed'); assert.equal(count(), 0);
});
function nestedBankPage(t, hiddenEntry = false, routes = {}) {
    const p = page(t, '<frameset><frame name="main"></frameset>');
    const main = page(t, `<div style="${hiddenEntry ? 'display:none' : ''}"><a id="entry" onclick="taskBar.createWin('bank-original')">活期账户</a></div><iframe id="business"></iframe>`, routes.mainUrl || origin + endpoint);
    const business = page(t, accounts, routes.businessUrl || origin + '/NCCB/NECV6B2BMainPlat_09_FW');
    Object.defineProperty(p.w.document.querySelector('frame'), 'contentDocument', { get: () => main.w.document });
    Object.defineProperty(main.w.document.querySelector('iframe'), 'contentDocument', { get: () => business.w.document });
    return { ...p, main: main.w.document, business: business.w.document };
}
test('opens the xc main frame after its declared b2bmainV7 source redirects to LH', t => {
    const currentOrigin = 'https://xc.b2b3.ccb.com';
    const p = page(t, `<frameset><frame src="${currentOrigin}/NCCB/V6/b2bmainV7.jsp"></frameset>`, currentOrigin + '/NCCB/V6/b2bindex.jsp');
    const main = page(t, '<a id="entry" onclick="taskBar.createWin()">活期账户</a>', currentOrigin + endpoint);
    const frame = p.w.document.querySelector('frame');
    Object.defineProperty(frame, 'contentDocument', { get: () => main.w.document });
    let clicks = 0;
    main.w.document.querySelector('#entry').addEventListener('click', () => clicks++);
    assert.match(frame.getAttribute('src'), /b2bmainV7\.jsp$/);
    assert.equal(p.run('open-accounts', { origin: currentOrigin }).status, 'opened');
    assert.equal(clicks, 1);
    assert.equal(p.run('open-accounts', { origin: currentOrigin }).status, 'waiting');
    assert.equal(clicks, 1);
});
test('stale declared source still requires a visible frame with an allowed actual document', t => {
    for (const kind of ['hidden', 'unsupported-path']) {
        const currentOrigin = 'https://xc.b2b3.ccb.com';
        const p = page(t, `<frameset><frame src="${currentOrigin}/NCCB/V6/b2bmainV7.jsp"></frameset>`, currentOrigin + '/NCCB/V6/b2bindex.jsp');
        const main = page(t, '<a id="entry" onclick="taskBar.createWin()">活期账户</a>', currentOrigin + (kind === 'unsupported-path' ? '/NCCB/outside' : endpoint));
        const frame = p.w.document.querySelector('frame');
        Object.defineProperty(frame, 'contentDocument', { get: () => main.w.document });
        if (kind === 'hidden') frame.style.display = 'none';
        let clicks = 0;
        main.w.document.querySelector('#entry').addEventListener('click', () => clicks++);
        assert.equal(p.run('open-accounts', { origin: currentOrigin }).status, 'unsupported');
        assert.equal(clicks, 0);
    }
});
test('missing current-account entry has a distinct fixed failure code', t => {
    const p = page(t, '<body></body>');
    assert.deepEqual(p.run('open-accounts'), { status: 'unsupported', stage: 'unsupported', code: 'entry-not-found' });
});
test('opens the original current-account entry in the nested main bank document', t => {
    const p = nestedBankPage(t); let clicks = 0;
    p.main.querySelector('#entry').addEventListener('click', () => clicks++);
    assert.equal(p.run('open-accounts').status, 'opened'); assert.equal(clicks, 1);
    assert.equal(p.run('select-first').status, 'selected');
});
test('opens a unique hidden navigation leaf in a visible trusted frame', t => {
    const p = nestedBankPage(t, true); let clicks = 0;
    p.main.querySelector('#entry').addEventListener('click', () => clicks++);
    assert.equal(p.run('open-accounts').status, 'opened'); assert.equal(clicks, 1);
});
test('prefers a unique visible entry and refuses ambiguous hidden navigation leaves', t => {
    const p = nestedBankPage(t, true); let hidden = 0, shown = 0;
    p.main.querySelector('#entry').addEventListener('click', () => hidden++);
    p.main.body.insertAdjacentHTML('beforeend', '<a id="shown" onclick="taskBar.createWin()">活期账户</a>');
    p.main.querySelector('#shown').addEventListener('click', () => shown++);
    assert.equal(p.run('open-accounts').status, 'opened'); assert.equal(shown, 1); assert.equal(hidden, 0);
    const q = nestedBankPage(t, true); q.main.querySelector('div').insertAdjacentHTML('beforeend', '<a onclick="taskBar.createWin()">活期账户</a>');
    assert.equal(q.run('open-accounts').status, 'ambiguous');
});
test('uses the nested document base URI and inherited blank base for relative form actions', t => {
    for (const inherited of [false, true]) {
        const p = nestedBankPage(t);
        if (inherited) {
            p.business.body.innerHTML = '<iframe></iframe>';
            const nested = p.business.querySelector('iframe').contentDocument;
            Object.defineProperty(nested.defaultView.Element.prototype, 'getClientRects', { value() { return [{}]; } });
            nested.body.innerHTML = accounts.replace(endpoint, 'NECV6B2BMainPlat_09_FW');
        } else {
            p.business.querySelector('form').setAttribute('action', '?existingBankParameter=unchanged');
        }
        assert.equal(p.run('open-accounts').status, 'opened'); assert.equal(p.run('select-first').status, 'selected');
    }
});
test('selects the first proven all-download owner among repeated top and bottom controls', t => {
    const p = page(t); p.queried();
    p.set(resultBase + '<div><a id="current">下载当前页</a><ul>' + pdf + '</ul></div><div><a id="dlall">下载全部</a><ul>' + pdf + '</ul></div><div><a id="dlAll">下载全部</a><ul>' + pdf + '</ul></div>');
    const current = p.countClicks('#current + ul li'), first = p.countClicks('#dlall + ul li'), last = p.countClicks('#dlAll + ul li');
    assert.equal(p.run('download').status, 'downloading'); assert.equal(first(), 1); assert.equal(last(), 0); assert.equal(current(), 0);
});
test('refuses download when result account or full-month dates do not match the submitted query', t => {
    for (const [field, value] of [['ACC_NO', 'WRONG_ACCOUNT'], ['StDt', '20260101'], ['EdDt', '20260227']]) {
        const p = page(t); p.queried(); p.set(resultBase + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>');
        p.child.querySelector(`[name="${field}"]`).value = value; const count = p.countClicks('li');
        const result = p.run('download'); assert.equal(result.status, 'failed'); assert.equal(result.code, 'result-identity'); assert.equal(count(), 0);
        assert.doesNotMatch(JSON.stringify(result), /WRONG_ACCOUNT|20260/);
    }
});
test('refuses missing or conflicting result identity, including print form copies', t => {
    for (const mutate of [doc => doc.querySelector('[name=ACC_NO]').remove(), doc => doc.querySelector('[name=StDt]').remove(), doc => doc.querySelector('[name=elecPrintForm]').insertAdjacentHTML('beforeend', '<input name="ACC_NO" type="hidden" value="OTHER">')]) {
        const p = page(t); p.queried(); p.set(resultBase + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>'); mutate(p.child);
        const count = p.countClicks('li'); const result = p.run('download'); assert.equal(result.status, 'failed'); assert.equal(result.code, 'result-identity'); assert.equal(count(), 0);
    }
});
test('rejects matching result metadata arriving in a different frame lineage', t => {
    const p = page(t); p.queried(); p.w.document.querySelector('iframe').remove();
    p.w.document.body.insertAdjacentHTML('beforeend', '<iframe></iframe>');
    const replacement = p.w.document.querySelector('iframe').contentDocument;
    Object.defineProperty(replacement.defaultView.Element.prototype, 'getClientRects', { value() { return [{}]; } });
    replacement.body.innerHTML = resultBase + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>';
    let clicks = 0; replacement.addEventListener('click', () => clicks++);
    const result = p.run('download'); assert.equal(result.status, 'failed'); assert.equal(result.code, 'result-lineage'); assert.equal(clicks, 0);
});
test('an existing result tab waits for account navigation without dispatching BacktoFirst', t => {
    const p = page(t); p.set(resultBase + '<input id="back" type="button" onclick="BacktoFirst()">'); const count = p.countClicks('#back');
    p.run('open-accounts'); assert.equal(p.run('select-first').status, 'waiting'); assert.equal(p.run('select-first').status, 'waiting'); assert.equal(count(), 0);
    p.set(accounts); assert.equal(p.run('select-first').status, 'selected');
});
test('an existing result tab needs no back control while awaiting the new account list', t => {
    const p = page(t); p.set(resultBase); p.run('open-accounts');
    assert.deepEqual(p.run('select-first'), { status: 'waiting', stage: 'results' });
});
test('an all-download ARIA reference cannot borrow the current-page sibling menu', t => {
    const p = page(t); p.queried();
    p.set(resultBase + '<a id="dlall" aria-controls="currentMenu">下载全部</a><div></div><a id="current">下载当前页</a><ul id="currentMenu">' + pdf + '</ul>');
    const count = p.countClicks('li'); assert.equal(p.run('download').status, 'ambiguous'); assert.equal(count(), 0);
});
test('result navigation may replace the document while preserving its original frame lineage', t => {
    const p = page(t); p.queried();
    const next = page(t, resultBase + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>', origin + endpoint);
    Object.defineProperty(p.w.document.querySelector('iframe'), 'contentDocument', { get: () => next.w.document });
    let clicks = 0; next.w.document.querySelector('li').addEventListener('click', () => clicks++);
    assert.equal(p.run('download').status, 'downloading'); assert.equal(clicks, 1);
});

// Bank taskBar uses task_0000/taskOn_div and win_0000/isReady for the homepage.
function navigationPage(t, active = false, routes = {}) {
    const p = nestedBankPage(t, false, routes);
    p.main.body.insertAdjacentHTML('afterbegin', '<div id="menu1Content"><a id="0000">首页</a></div><div id="task_0000" class="taskOff_div"><span>首页</span><a>×</a></div><iframe id="win_0000" isReady="false" style="display:none"></iframe>');
    const nav = p.main.querySelector('[id="0000"]'), tab = p.main.querySelector('#task_0000'), frame = p.main.querySelector('#win_0000');
    let homeClicks = 0, accountClicks = 0;
    nav.addEventListener('click', () => homeClicks++);
    p.main.querySelector('#entry').addEventListener('click', () => accountClicks++);
    const finish = () => {
        tab.className = 'taskOn_div'; frame.style.display = ''; frame.setAttribute('isReady', 'true');
        p.main.querySelector('#business').style.display = 'none';
    };
    if (active) finish();
    return { ...p, nav, tab, frame, finish, homeClicks: () => homeClicks, accountClicks: () => accountClicks };
}

for (const version of ['08', '12']) {
    test(`statement returns home and exports through numeric bank route version ${version}`, t => {
        const route = `/NCCB/NECV6B2BMainPlat_${version}_FW`;
        const p = navigationPage(t, true, { mainUrl: origin + `/NCCB/NECV6B2BMainPlat_${version}_LH`, businessUrl: origin + route });
        const account = '1234567890120007', company = '示例服务有限公司';
        const adapt = html => html.replaceAll(endpoint, route).replaceAll('PRIVATE_FIRST', account + '@' + company + '@人民币@156@0@0@1');
        assert.equal(p.run('ensure-home').status, 'home-ready');
        assert.equal(p.run('open-accounts').status, 'opened');
        p.main.querySelector('#business').style.display = '';
        p.business.body.innerHTML = adapt(accounts);
        assert.equal(p.run('select-first').status, 'selected');
        p.business.body.innerHTML = adapt(query);
        p.business.querySelector('[name=Submit4]').addEventListener('click', event => event.preventDefault());
        assert.equal(p.run('query').status, 'queried');
        p.business.body.innerHTML = adapt(resultBase).replace('</form>', `<table id="headTable" class="tableConfirm"><tr><td class="cfmTitle cfmSty1">账号：</td><td class="cfmInfo cfmSty4 acc">${account}</td><td class="cfmTitle cfmSty1">账户名称：</td><td class="cfmInfo cfmSty4 fzWord">${company}</td></tr></table><input name="checkedAccno" type="hidden" value="${account}"><input name="checkedAccName" type="hidden" value="${company}"></form>`) + '<a id="dlall">下载全部</a><ul>' + pdf + '</ul>';
        let downloads = 0; p.business.querySelector('li').addEventListener('click', () => downloads++);
        assert.deepEqual(p.run('identify'), { status: 'identified', stage: 'results', companyName: company, accountLast4: '0007' });
        assert.equal(p.run('download').status, 'downloading');
        p.run('download'); assert.equal(downloads, 1);
    });
}

test('statement route compatibility still excludes foreign origins protocols and unrelated paths', t => {
    const badUrls = ['http://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH', 'https://outside.example/NCCB/NECV6B2BMainPlat_08_LH', origin + '/NCCB/NECV6B2BMainPlat_preview_LH', origin + '/NCCB/NECV6B2BMainPlat_08_LH/extra', origin + '/NCCB/NECV6B2BMainPlat_08_OTHER'];
    for (const mainUrl of badUrls) {
        const p = navigationPage(t, true, { mainUrl });
        assert.notEqual(p.run('ensure-home').status, 'home-ready'); assert.equal(p.accountClicks(), 0);
    }
    for (const action of badUrls) {
        const p = page(t); p.run('open-accounts'); p.set(accounts); p.child.querySelector('form').action = action;
        const clicks = p.countClicks('[name=Submit3]');
        assert.notEqual(p.run('select-first').status, 'selected'); assert.equal(clicks(), 0);
    }
});

test('returns home once from a query page and waits for the homepage frame before opening accounts', t => {
    const p = navigationPage(t); p.business.body.innerHTML = query;
    assert.equal(p.run('ensure-home').status, 'waiting');
    assert.equal(p.run('ensure-home').status, 'waiting');
    assert.equal(p.homeClicks(), 1); assert.equal(p.accountClicks(), 0);
    p.tab.className = 'taskOn_div'; p.frame.style.display = '';
    assert.equal(p.run('ensure-home').status, 'waiting'); // frame still loading
    p.finish();
    assert.equal(p.run('ensure-home').status, 'home-ready');
    assert.equal(p.run('open-accounts').status, 'opened');
    assert.equal(p.homeClicks(), 1); assert.equal(p.accountClicks(), 1);
});

test('already active homepage skips navigation including an old hidden results tab', t => {
    const p = navigationPage(t, true); p.business.body.innerHTML = resultBase;
    assert.equal(p.run('ensure-home').status, 'home-ready');
    assert.equal(p.homeClicks(), 0);
    assert.equal(p.run('open-accounts').status, 'opened');
    assert.equal(p.accountClicks(), 1);
});

test('home navigation ignores pagination and refuses missing duplicate or disabled main navigation', t => {
    for (const kind of ['missing', 'duplicate', 'disabled', 'hidden']) {
        const p = navigationPage(t); let wrong = 0;
        p.business.body.insertAdjacentHTML('beforeend', '<a id="pagination">首页</a>');
        p.business.querySelector('#pagination').addEventListener('click', () => wrong++);
        if (kind === 'missing') p.nav.remove();
        if (kind === 'duplicate') p.nav.after(p.nav.cloneNode(true));
        if (kind === 'disabled') p.nav.setAttribute('aria-disabled', 'true');
        if (kind === 'hidden') p.nav.style.display = 'none';
        assert.ok(['unsupported', 'ambiguous'].includes(p.run('ensure-home').status));
        assert.equal(p.homeClicks(), 0); assert.equal(wrong, 0); assert.equal(p.accountClicks(), 0);
    }
});

test('home navigation consumes its click before synchronous reentry and honors disposal expiry and conflicts', t => {
    const p = navigationPage(t); const nested = [];
    p.nav.addEventListener('click', () => nested.push(p.run('ensure-home').status));
    assert.equal(p.run('ensure-home').status, 'waiting');
    assert.deepEqual(nested, ['waiting']); assert.equal(p.homeClicks(), 1);
    assert.equal(p.run('ensure-home', { runId: 'another-run' }).code, 'run-conflict');
    p.run('dispose'); assert.equal(p.run('ensure-home').status, 'failed');
    assert.equal(p.homeClicks(), 1);
    const expired = navigationPage(t);
    assert.equal(expired.run('ensure-home', { expiresAt: 0 }).status, 'failed');
    assert.equal(expired.homeClicks(), 0);
});

test('a later export can return home again after the previous run was disposed', t => {
    const p = navigationPage(t, true);
    assert.equal(p.run('ensure-home').status, 'home-ready');
    assert.equal(p.run('open-accounts').status, 'opened');
    p.run('dispose');
    p.tab.className = 'taskOff_div'; p.frame.style.display = 'none';
    assert.equal(p.run('ensure-home', { runId: 'run-two' }).status, 'waiting');
    assert.equal(p.homeClicks(), 1);
    p.finish();
    assert.equal(p.run('ensure-home', { runId: 'run-two' }).status, 'home-ready');
    assert.equal(p.run('open-accounts', { runId: 'run-two' }).status, 'opened');
    assert.equal(p.accountClicks(), 2);
});

test('three exports reuse a bank tab without letting stale results or query interrupt account navigation', t => {
    const p = navigationPage(t, true);
    let backClicks = 0, selectionClicks = 0, queryClicks = 0;
    for (let month = 1; month <= 3; month++) {
        const dates = { runId: 'repeat-' + month, startDate: `20260${month}01`, endDate: `20260${month}${month === 2 ? '28' : '31'}` };
        const run = operation => p.run(operation, dates);
        p.finish();
        assert.equal(run('ensure-home').status, 'home-ready');
        p.business.body.innerHTML = resultBase + '<button onclick="BacktoFirst()">返回</button>';
        p.business.querySelector('button').addEventListener('click', () => backClicks++);
        assert.equal(run('open-accounts').status, 'opened');
        p.main.querySelector('#business').style.display = '';
        assert.equal(run('select-first').status, 'waiting');
        assert.equal(backClicks, 0);
        p.business.body.innerHTML = query + '<button onclick="BacktoFirst()">返回</button>';
        p.business.querySelector('button').addEventListener('click', () => backClicks++);
        assert.equal(run('select-first').status, 'waiting');
        assert.equal(run('query').code, 'missing-selection'); // Old query cannot prove the first account.
        assert.equal(backClicks, 0);
        p.business.body.innerHTML = accounts;
        p.business.querySelector('[name=Submit3]').addEventListener('click', () => {
            selectionClicks++;
            p.business.body.innerHTML = query;
            p.business.querySelector('[name=Submit4]').addEventListener('click', event => { event.preventDefault(); queryClicks++; });
        });
        assert.equal(run('select-first').status, 'selected');
        assert.equal(run('inspect').stage, 'query');
        assert.equal(run('query').status, 'queried');
        assert.equal(p.business.querySelector('[name=StDt]').value, dates.startDate);
        assert.equal(p.business.querySelector('[name=EdDt]').value, dates.endDate);
        assert.equal(run('query').status, 'waiting');
        assert.equal(selectionClicks, month); assert.equal(queryClicks, month);
        run('dispose');
    }
});
