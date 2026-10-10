const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const source = fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbReceiptExport.js'), 'utf8');
const origin = 'https://b2b3.ccb.com';
const endpoint = '/NCCB/NECV6B2BMainPlat_09_LH';
const company = '示例物业服务有限公司工会', account = '1234567890120007';
// Technical field/handler identities are from live-evidence.jsonl (2026-10-08).
// Business values, radio wrappers, and paging variations below are synthetic contracts,
// not a claim that their exact markup was captured from the bank.
const queryHtml = `<form id="jhform" name="jhform" action="${endpoint}" method="post" onsubmit="return go()">
<select id="hdType" name="hdType" onchange="changeDetailType()"><option value="batch">批量回单查询</option><option value="other">其他</option></select>
<input id="BEGIN_DATE" name="BEGIN_DATE"><input id="END_DATE" name="END_DATE">
<input id="MIN_AMT" name="MIN_AMT" value="99"><input id="MAX_AMT" name="MAX_AMT" value="100">
<select id="ACCT_NO" name="ACCT_NO"><option value="other-account">999988887777另一公司</option><option value="receipt-account">${account} ${company}</option></select>
<input id="ser" name="text" value="old search"><select id="PRINT_FLAG" name="PRINT_FLAG"><option value="printed">已打印</option><option value="all">全部</option></select>
<select name="DEAL_DIRECTION"><option value="out">支出</option><option value="all">全部</option></select><input name="DOC_NO" value="old-document">
<label><input type="radio" name="Data_Fmt" value="pdf" onclick="setTouchData()">PDF</label><label><input type="radio" name="Data_Fmt" value="ofd" checked>OFD</label>
<label><input type="radio" name="Mrg_Dwld_Ind" value="yes">是</label><label><input type="radio" name="Mrg_Dwld_Ind" value="no" checked>否</label>
<input id="subbutton" name="Submit1" type="submit"><input id="gzbutton" type="button" onclick="genzheng()"></form>`;
function resultsHtml({ rows = 2, page = 1, pages = 1, all = false } = {}) {
    return `<form name="jhform" method="post" action="${endpoint}" onsubmit="return go()"><div class="titleTxt">批量回单查询结果</div>
    <input type="hidden" name="ACCT_NO" value="receipt-account"><input type="hidden" name="BEGIN_DATE" value="20260201"><input type="hidden" name="END_DATE" value="20260228">
    <input type="hidden" name="Data_Fmt" value="pdf"><input type="hidden" name="Mrg_Dwld_Ind" value="yes"><input type="hidden" name="PRINT_FLAG" value="all"><input type="hidden" name="DEAL_DIRECTION" value="all"><input type="hidden" name="MIN_AMT"><input type="hidden" name="MAX_AMT"><input type="hidden" name="DOC_NO">
    ${Array.from({ length: rows }, (_, i) => `<input type="checkbox" class="checkbox" name="KEYVALUE" value="PRIVATE_RECEIPT_${i}" onclick="trClick()" ${all ? 'checked' : ''}>`).join('')}
    <input type="checkbox" name="all" onclick="selectall()" ${all ? 'checked' : ''}><span>全选</span>
    <div class="splitPage"><a href="javascript:TxtSubmit()">首页</a>${page === 1 ? '<span>上一页</span>' : '<a href="javascript:TxtSubmit()">上一页</a>'}<span>${page}</span>${pages > page ? '<a href="javascript:TxtSubmit()">下一页</a>' : '<span>下一页</span>'}共${pages}页 到第<input class="inPage" value="999"><a class="pageGoTo" href="javascript:jumpToPage()">转至</a></div>
    <input id="Submit8" name="Submit8" type="button" onclick="HDPrint()"><input type="button" name="sc2" onclick="downAll()"></form>`;
}
function fixture(t, routes = {}) {
    const dom = new JSDOM('<iframe id="main"></iframe>', { url: origin + '/NCCB/V6/b2bindex.jsp', runScripts: 'outside-only' });
    t.after(() => dom.window.close());
    const w = dom.window;
    const show = doc => {
        Object.defineProperty(doc.defaultView.Element.prototype, 'getClientRects', { configurable: true, value() { return [{ width: 100, height: 20 }]; } });
        Object.defineProperty(doc, 'readyState', { configurable: true, get: () => 'complete' });
    };
    show(w.document);
    const framedDocument = (frame, url) => {
        if (!url) return frame.contentDocument;
        const child = new JSDOM('', { url, runScripts: 'outside-only' }); t.after(() => child.window.close());
        Object.defineProperty(frame, 'contentDocument', { configurable: true, get: () => child.window.document });
        return child.window.document;
    };
    const main = framedDocument(w.document.querySelector('iframe'), routes.mainUrl); show(main);
    main.body.innerHTML = '<div id="menu1Content"><a id="0000" onclick="taskBar.createWin()">首页</a><a id="account-nav" onclick="SJCJ_MENU_1()">账户查询</a></div><div id="task_0000" class="taskOn_div"></div><a id="receipt-nav" onclick="taskBar.createWin()">电子回单查询</a><iframe id="win_0000" isReady="true"></iframe><iframe id="business"></iframe>';
    const home = framedDocument(main.querySelector('#win_0000'), routes.homeUrl); show(home);
    home.body.innerHTML = `<div class="accInfoDiv"><div class="accDetailDiv"><div class="accInfo"><span id="ACCNM_ZHGS" class="deptName">${company}</span><span id="ACCNO_ZHGS" class="accNo">${account}</span></div></div></div>`;
    Object.defineProperty(home, 'readyState', { configurable: true, get: () => 'complete' });
    let doc = main.querySelector('#business').contentDocument; show(doc);
    const frame = main.querySelector('#business');
    const documents = [];
    const set = (html, replace = false) => {
        html = html.replaceAll(endpoint, routes.action || endpoint);
        if (replace) {
            const child = new JSDOM(html, { url: routes.businessUrl || origin + endpoint, runScripts: 'outside-only' });
            documents.push(child); t.after(() => child.window.close()); doc = child.window.document; show(doc);
            Object.defineProperty(frame, 'contentDocument', { configurable: true, get: () => doc });
        } else doc.body.innerHTML = html;
        doc.querySelectorAll('[name="PER_PAGE"]').forEach(node => node.addEventListener('click', () => {
            const merge = doc.querySelector('[name="Mrg_Dwld_Ind"]');
            merge.value = node.value === '1' ? '2' : '1';
        }));
        return doc;
    };
    const clicks = { home: 0, account: 0, open: 0, query: 0, all: 0, print: 0, page: 0, downloadAll: 0 };
    main.querySelector('[id="0000"]').addEventListener('click', () => { clicks.home++; main.querySelector('#task_0000').className = 'taskOn_div'; });
    main.querySelector('#account-nav').addEventListener('click', () => clicks.account++);
    main.querySelector('#receipt-nav').addEventListener('click', () => { clicks.open++; set(routes.queryHtml || queryHtml, true); doc.querySelector('#subbutton').addEventListener('click', e => { e.preventDefault(); clicks.query++; }); });
    const run = (operation, extra = {}) => JSON.parse(JSON.stringify(w.eval(`(${source})`)({ operation, runId: 'receipt-one', origin, startDate: '20260201', endDate: '20260228', expiresAt: Date.now() + 60000, ...extra })));
    const queried = () => { assert.equal(run('ensure-home').status, 'home-ready'); for (let i = 0; i < 3 && !clicks.open; i++) run('open'); assert.equal(run('query').status, 'queried'); };
    const results = (options = {}) => {
        if (routes.queryHtml) {
            const controls = [...doc.querySelectorAll('input,select')].map(node => ({ value: node.value, checked: node.checked }));
            const suffix = resultsHtml(options).slice(resultsHtml(options).indexOf('<input type="checkbox"'));
            set(doc.querySelector('form').outerHTML.replace('</form>', '') + suffix, true);
            [...doc.querySelectorAll('input,select')].slice(0, controls.length).forEach((node, i) => {
                node.value = controls[i].value;
                if ('checked' in node) node.checked = controls[i].checked;
            });
        } else set(resultsHtml(options), true);
        doc.querySelector('[name="all"]').addEventListener('click', () => { clicks.all++; doc.querySelectorAll('[name="KEYVALUE"]').forEach(n => n.checked = true); });
        doc.querySelector('#Submit8').addEventListener('click', () => clicks.print++);
        doc.querySelector('[name="sc2"]').addEventListener('click', () => clicks.downloadAll++);
        doc.querySelectorAll('.splitPage a').forEach(n => n.addEventListener('click', e => { e.preventDefault(); clicks.page++; }));
    };
    return { w, main, home, frame, get doc() { return doc; }, set, run, clicks, queried, results };
}

// Combined query/results template observed on 2026-10-10; business values are synthetic.
const combinedQueryHtml = queryHtml.replace('id="jhform" ', '')
    .replaceAll('hdType', 'FLAG').replace('value="batch"', 'value="2"')
    .replace('id="ser"', 'id="down_ser"')
    .replace('<select name="DEAL_DIRECTION"><option value="out">支出</option><option value="all">全部</option></select>',
        '<label><input id="fangx1" type="checkbox" name="DEAL_DIRECTION" value="1">借方</label><label><input id="fangx2" type="checkbox" name="DEAL_DIRECTION" value="2" checked>贷方</label>')
    .replace('<label><input type="radio" name="Mrg_Dwld_Ind" value="yes">是</label><label><input type="radio" name="Mrg_Dwld_Ind" value="no" checked>否</label>',
        '<input name="Mrg_Dwld_Ind" type="hidden"><label><input type="radio" name="PER_PAGE" value="1" onclick="javascript:changeHBType(0)">非合并(单联)</label><label><input type="radio" name="PER_PAGE" value="2" onclick="javascript:changeHBType(1)">合并(单联)</label><label><input type="radio" name="PER_PAGE" value="3" checked onclick="javascript:changeHBType(2)">合并(三联)</label>')
    .replace('</form>', '<div hidden><select name="PRINT_FLAG"><option value="hidden">全部</option></select></div><div class="titleTxt">回单查询结果</div></form>');

test('combined query uses visible controls and both directions without changing hidden alternate template', t => {
    const p = fixture(t, { queryHtml: combinedQueryHtml });
    p.queried();
    assert.equal(p.clicks.query, 1);
    assert.equal(p.doc.querySelector('[name=FLAG]').value, '2');
    assert.equal(p.doc.querySelector('[name=BEGIN_DATE]').value, '20260201');
    assert.equal(p.doc.querySelector('[name=END_DATE]').value, '20260228');
    assert.equal(p.doc.querySelector('[name=ACCT_NO]').value, 'receipt-account');
    assert.ok([...p.doc.querySelectorAll('[name=DEAL_DIRECTION]')].every(n => n.checked));
    assert.equal(p.doc.querySelector('[name=PER_PAGE]:checked').value, '3');
    assert.equal(p.doc.querySelector('[name=Mrg_Dwld_Ind]').value, '1');
    assert.equal(p.doc.querySelector('[name=Data_Fmt]:checked').value, 'pdf');
    assert.equal(p.doc.querySelector('[hidden] [name=PRINT_FLAG]').value, 'hidden');
    assert.equal(p.doc.querySelector('#down_ser').value, '');
    assert.equal(p.run('identify').status, 'waiting');
    p.run('query'); assert.equal(p.clicks.query, 1);
});

test('combined results retain editable query controls and download only first page once', t => {
    const p = fixture(t, { queryHtml: combinedQueryHtml, action: '/NCCB/NECV6B2BMainPlat_08_LH' });
    p.queried(); p.results({ rows: 7, pages: 4 });
    assert.deepEqual(p.run('identify'), { status: 'identified', stage: 'results', companyName: company, accountLast4: '0007' });
    assert.equal(p.run('download').status, 'downloading');
    assert.equal(p.run('download').status, 'downloading');
    assert.equal(p.run('verify-download').status, 'verified');
    assert.equal(p.clicks.all, 1); assert.equal(p.clicks.print, 1);
    assert.equal(p.clicks.page, 0); assert.equal(p.clicks.downloadAll, 0);
});

test('combined results reject changed account month direction format merge or result type', t => {
    for (const mutate of [
        p => p.doc.querySelector('[name=ACCT_NO]').value = 'other-account',
        p => p.doc.querySelector('[name=END_DATE]').value = '20260131',
        p => p.doc.querySelector('[name=DEAL_DIRECTION]').checked = false,
        p => p.doc.querySelector('[name=Data_Fmt][value=ofd]').checked = true,
        p => p.doc.querySelector('[name=Mrg_Dwld_Ind]').value = '',
        p => p.doc.querySelector('[name=PER_PAGE][value="1"]').checked = true,
        p => p.doc.querySelector('[name=FLAG]').value = 'other',
        p => p.doc.querySelector('[name=MIN_AMT]').value = '100'
    ]) {
        const p = fixture(t, { queryHtml: combinedQueryHtml }); p.queried(); p.results(); mutate(p);
        assert.equal(p.run('identify').status, 'failed');
        assert.equal(p.run('download').status, 'failed'); assert.equal(p.clicks.print, 0);
    }
});

test('combined empty results require fresh matching month and never print', t => {
    const p = fixture(t, { queryHtml: combinedQueryHtml }); p.queried();
    assert.equal(p.run('identify').status, 'waiting');
    p.results({ rows: 0 });
    assert.equal(p.run('identify').status, 'empty'); assert.equal(p.clicks.print, 0);
    p.doc.querySelector('[name=BEGIN_DATE]').value = '20260101';
    assert.equal(p.run('identify').status, 'failed');
});

test('combined query requires original merge initialization and rejects duplicate visible filters', t => {
    for (const mutate of [
        p => p.doc.querySelector('[name=PER_PAGE][value="3"]').removeAttribute('onclick'),
        p => p.doc.querySelector('[name=PER_PAGE][value="3"]').addEventListener('click', () => p.doc.querySelector('[name=Mrg_Dwld_Ind]').value = ''),
        p => p.doc.querySelector('[hidden]').hidden = false
    ]) {
        const p = fixture(t, { queryHtml: combinedQueryHtml }); p.run('ensure-home'); p.run('open'); p.run('open'); mutate(p);
        assert.equal(p.run('query').status, 'failed'); assert.equal(p.clicks.query, 0);
    }
});

for (const version of ['08', '12']) {
    test(`receipt returns home and downloads only first page through numeric bank route version ${version}`, t => {
        const route = `/NCCB/NECV6B2BMainPlat_${version}_FW`;
        const p = fixture(t, { mainUrl: origin + `/NCCB/NECV6B2BMainPlat_${version}_LH`, homeUrl: origin + route, businessUrl: origin + route, action: route });
        p.queried(); p.results({ pages: 5 });
        assert.deepEqual(p.run('identify'), { status: 'identified', stage: 'results', companyName: company, accountLast4: '0007' });
        assert.equal(p.run('download').status, 'downloading');
        p.run('download'); assert.equal(p.clicks.all, 1); assert.equal(p.clicks.print, 1);
        assert.equal(p.clicks.page, 0); assert.equal(p.clicks.downloadAll, 0);
        assert.equal(p.run('verify-download').status, 'verified');
    });
}

test('receipt route compatibility still excludes foreign origins protocols and unrelated paths', t => {
    const badUrls = ['http://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH', 'https://outside.example/NCCB/NECV6B2BMainPlat_08_LH', origin + '/NCCB/NECV6B2BMainPlat_preview_LH', origin + '/NCCB/NECV6B2BMainPlat_08_LH/extra', origin + '/NCCB/NECV6B2BMainPlat_08_OTHER'];
    for (const mainUrl of badUrls) {
        const p = fixture(t, { mainUrl });
        assert.notEqual(p.run('ensure-home').status, 'home-ready'); assert.equal(p.clicks.account, 0);
    }
    for (const action of badUrls) {
        const p = fixture(t, { action }); p.run('ensure-home'); p.run('open'); p.run('open');
        assert.notEqual(p.run('query').status, 'queried'); assert.equal(p.clicks.query, 0);
    }
});
test('receipt waits for complete query and result documents before checking their partially parsed fields', t => {
    const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
    const accountSelect = p.doc.querySelector('#ACCT_NO'), accountOption = accountSelect.options[1];
    accountOption.remove();
    let readyState = 'loading';
    Object.defineProperty(p.doc, 'readyState', { configurable: true, get: () => readyState });
    for (const phase of ['loading', 'interactive']) {
        readyState = phase;
        assert.equal(p.run('query').status, 'waiting'); assert.equal(p.clicks.query, 0);
        assert.equal(p.doc.querySelector('#BEGIN_DATE').value, '');
    }
    accountSelect.append(accountOption); readyState = 'complete';
    assert.equal(p.run('query').status, 'queried'); assert.equal(p.clicks.query, 1);
    p.results();
    const format = p.doc.querySelector('[name=Data_Fmt]'); format.remove();
    readyState = 'loading';
    Object.defineProperty(p.doc, 'readyState', { configurable: true, get: () => readyState });
    for (const phase of ['loading', 'interactive']) {
        readyState = phase;
        assert.equal(p.run('identify').status, 'waiting');
        assert.equal(p.run('download').status, 'waiting'); assert.equal(p.clicks.print, 0);
    }
    p.doc.querySelector('form').append(format); readyState = 'complete';
    assert.equal(p.run('identify').status, 'identified');
    assert.equal(p.run('download').status, 'downloading'); assert.equal(p.clicks.print, 1);
});

test('returns home before receipt navigation and fills exact month and observed filters once', t => {
    const p = fixture(t); p.main.querySelector('#task_0000').className = '';
    assert.equal(p.run('ensure-home').status, 'waiting'); assert.equal(p.clicks.home, 1);
    p.queried();
    assert.equal(p.clicks.account, 1); assert.equal(p.clicks.open, 1); assert.equal(p.clicks.query, 1);
    for (const [name, value] of [['BEGIN_DATE','20260201'], ['END_DATE','20260228'], ['MIN_AMT',''], ['MAX_AMT',''], ['DOC_NO',''], ['ACCT_NO','receipt-account'], ['PRINT_FLAG','all'], ['DEAL_DIRECTION','all']]) assert.equal(p.doc.querySelector(`[name="${name}"]`).value, value);
    assert.equal(p.doc.querySelector('[name="Data_Fmt"]:checked').value, 'pdf');
    assert.equal(p.doc.querySelector('[name="Mrg_Dwld_Ind"]:checked').value, 'yes');
    assert.equal(p.run('query').status, 'queried'); assert.equal(p.clicks.query, 1);
});
test('matches full account and company despite option order; returns only filename identity', t => {
    const p = fixture(t); p.queried(); p.results();
    assert.deepEqual(p.run('identify'), { status: 'identified', stage: 'results', companyName: company, accountLast4: '0007' });
    assert.doesNotMatch(JSON.stringify(p.run('identify')), /123456789012|PRIVATE_RECEIPT/);
});
test('selects only first page and invokes HDPrint exactly once, never paging or download-all', t => {
    const p = fixture(t); p.queried(); p.results({ pages: 5 });
    assert.equal(p.run('identify').status, 'identified');
    assert.equal(p.run('download').status, 'downloading');
    assert.equal(p.run('download').status, 'downloading');
    assert.equal(p.clicks.all, 1); assert.equal(p.clicks.print, 1); assert.equal(p.clicks.page, 0); assert.equal(p.clicks.downloadAll, 0);
    assert.equal(p.run('verify-download').status, 'verified');
});
test('does not toggle an already fully selected page off', t => {
    const p = fixture(t); p.queried(); p.results({ all: true }); p.run('identify');
    assert.equal(p.run('download').status, 'downloading'); assert.equal(p.clicks.all, 0); assert.equal(p.clicks.print, 1);
});
test('rejects stale account dates or non-first-page without printing', t => {
    for (const mutate of [p => p.doc.querySelector('[name="ACCT_NO"]').value = 'other-account', p => p.doc.querySelector('[name="BEGIN_DATE"]').value = '20260101', p => p.doc.querySelector('[name="END_DATE"]').value = '20260131', p => { p.doc.querySelector('.splitPage span').outerHTML = '<a href="javascript:TxtSubmit()">上一页</a>'; }]) {
        const p = fixture(t); p.queried(); p.results(); mutate(p);
        assert.notEqual(p.run('identify').status, 'identified'); assert.notEqual(p.run('download').status, 'downloading'); assert.equal(p.clicks.print, 0);
    }
});
test('reports no receipts only after fresh query identity, without printing', t => {
    const p = fixture(t); p.queried(); p.results({ rows: 0 });
    assert.equal(p.run('identify').status, 'empty'); assert.equal(p.clicks.print, 0);
    p.doc.querySelector('[name="ACCT_NO"]').value = 'wrong'; assert.notEqual(p.run('identify').status, 'empty');
});
test('rejects duplicate matching accounts and ambiguous PDF labels before submission', t => {
    for (const mutate of [p => { const s = p.doc.querySelector('#ACCT_NO'); s.append(s.options[1].cloneNode(true)); }, p => { p.doc.querySelector('[name="Data_Fmt"][value="ofd"]').parentElement.lastChild.nodeValue = 'PDF'; }]) {
        const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open'); mutate(p);
        assert.equal(p.run('query').status, 'failed'); assert.equal(p.clicks.query, 0);
    }
});
test('will not print a pre-existing result or accept another frame lineage', t => {
    const p = fixture(t); p.results(); assert.notEqual(p.run('identify').status, 'identified');
    const q = fixture(t); q.queried(); q.results(); q.frame.remove();
    const other = q.main.createElement('iframe'); q.main.body.append(other);
    Object.defineProperty(other, 'contentDocument', { get: () => q.doc });
    assert.notEqual(q.run('identify').status, 'identified'); assert.equal(q.clicks.print, 0);
});
test('consumes select and print before reentrant handlers; rechecks selection after original handler', t => {
    const p = fixture(t); p.queried(); p.results(); p.run('identify');
    p.doc.querySelector('[name="all"]').addEventListener('click', () => p.run('download'));
    p.doc.querySelector('#Submit8').addEventListener('click', () => p.run('download'));
    assert.equal(p.run('download').status, 'downloading'); assert.equal(p.clicks.all, 1); assert.equal(p.clicks.print, 1);
    const q = fixture(t); q.queried(); q.results(); q.run('identify');
    q.doc.querySelector('[name="all"]').addEventListener('click', () => { q.doc.querySelector('[name="KEYVALUE"]').checked = false; });
    assert.equal(q.run('download').status, 'failed'); assert.equal(q.clicks.print, 0);
});
test('verify-download rejects replacement results and disposal clears page identity', t => {
    const p = fixture(t); p.queried(); p.results(); p.run('identify'); p.run('download');
    p.results({ all: true }); assert.notEqual(p.run('verify-download').status, 'verified');
    assert.equal(p.run('dispose').status, 'disposed'); assert.equal(p.w.document.__usbWebLauncherReceiptExportV1.account, null);
    assert.notEqual(p.run('download').status, 'downloading');
});
test('expired requests or conflicting run IDs never click', t => {
    const p = fixture(t);
    assert.equal(p.run('ensure-home', { expiresAt: 0 }).status, 'failed'); assert.equal(p.clicks.home, 0);
    assert.equal(p.run('ensure-home').status, 'home-ready');
    assert.equal(p.run('open', { runId: 'another' }).status, 'failed'); assert.equal(p.clicks.open, 0);
});
test('rejects changed result filters even when account and month still agree', t => {
    for (const name of ['Data_Fmt', 'Mrg_Dwld_Ind', 'PRINT_FLAG', 'DEAL_DIRECTION', 'MIN_AMT', 'MAX_AMT', 'DOC_NO']) {
        const p = fixture(t); p.queried(); p.results(); p.doc.querySelector(`[name="${name}"]`).value = 'different';
        assert.equal(p.run('identify').status, 'failed', name); assert.equal(p.clicks.print, 0);
    }
});
test('uses label-for and adjacent fixed text for radio selection, never option position', t => {
    for (const mode of ['label-for', 'adjacent']) {
        const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
        const original = p.doc.querySelector('[name="Data_Fmt"][value="pdf"]');
        if (mode === 'label-for') original.parentElement.outerHTML = '<input id="pdf-choice" type="radio" name="Data_Fmt" value="pdf"><label for="pdf-choice">PDF</label>';
        else original.parentElement.outerHTML = '<span><input type="radio" name="Data_Fmt" value="pdf">PDF</span>';
        assert.equal(p.run('query').status, 'queried'); assert.equal(p.doc.querySelector('[name="Data_Fmt"]:checked').value, 'pdf');
    }
});
test('prevented submit does not replay; only a new matching result can complete the query', t => {
    const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
    const button = p.doc.querySelector('#subbutton');
    button.replaceWith(button.cloneNode(true)); // Remove the fixture's synthetic navigation click hook.
    p.doc.querySelector('form').addEventListener('submit', e => e.preventDefault());
    let submissions = 0; p.doc.querySelector('form').addEventListener('submit', () => submissions++);
    assert.equal(p.run('query').status, 'queried');
    assert.equal(p.run('query').status, 'queried'); assert.equal(submissions, 1);
    assert.equal(p.run('identify').status, 'waiting');
    p.results(); assert.equal(p.run('identify').status, 'identified');
});
test('repeated exports start a fresh query after disposal', t => {
    const p = fixture(t); p.queried(); p.results(); p.run('identify'); p.run('download'); p.run('dispose');
    assert.equal(p.run('ensure-home', { runId: 'receipt-two' }).status, 'home-ready');
    p.run('open', { runId: 'receipt-two' }); assert.equal(p.run('open', { runId: 'receipt-two' }).status, 'opened');
    assert.equal(p.run('query', { runId: 'receipt-two' }).status, 'queried'); assert.equal(p.clicks.query, 2); assert.equal(p.clicks.print, 1);
});
test('does not print after checkbox handlers change the month or replace the print handler', t => {
    for (const mutation of [p => p.doc.querySelector('[name="BEGIN_DATE"]').value = '20260101', p => p.doc.querySelector('#Submit8').setAttribute('onclick', 'downAll()')]) {
        const p = fixture(t); p.queried(); p.results(); p.run('identify');
        p.doc.querySelector('[name="all"]').addEventListener('click', () => mutation(p));
        assert.equal(p.run('download').status, 'failed'); assert.equal(p.clicks.print, 0);
    }
});
test('rejects action changed by a date handler before clicking the original submit button', t => {
    const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
    p.doc.querySelector('#BEGIN_DATE').addEventListener('change', () => p.doc.querySelector('form').action = 'https://outside.example/collect');
    assert.equal(p.run('query').status, 'failed'); assert.equal(p.clicks.query, 0);
});
test('validates leap-year and complete calendar months before changing the query', t => {
    for (const [startDate, endDate, expected] of [['20240201','20240229','queried'], ['20260201','20260229','failed'], ['20260202','20260228','failed'], ['20261301','20261331','failed']]) {
        const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
        assert.equal(p.run('query', { startDate, endDate }).status, expected);
    }
});
test('rejects unknown origin and hidden receipt frames without actions', t => {
    const p = fixture(t); assert.equal(p.run('ensure-home', { origin: 'https://other.ccb.com' }).status, 'unsupported'); assert.equal(p.clicks.home, 0);
    const q = fixture(t); q.queried(); q.results(); q.frame.style.display = 'none';
    assert.notEqual(q.run('identify').status, 'identified'); assert.equal(q.clicks.print, 0);
});
test('leaves an already matching query type unchanged without replaying changeDetailType', t => {
    const p = fixture(t); p.run('ensure-home'); p.run('open'); p.run('open');
    p.doc.querySelector('#hdType').addEventListener('change', () => { p.doc.querySelector('form').action = 'https://outside.example/redundant-change'; });
    assert.equal(p.run('query').status, 'queried'); assert.equal(p.clicks.query, 1);
});
test('reused task tab waits through old results and old query until original navigation replaces the document', t => {
    const p = fixture(t); p.queried(); p.results(); p.run('identify'); p.run('download'); p.run('dispose');
    p.run('ensure-home', { runId: 'receipt-two' });
    const entry = p.main.querySelector('#receipt-nav'); entry.replaceWith(entry.cloneNode(true));
    p.main.querySelector('#receipt-nav').addEventListener('click', () => p.clicks.open++);
    p.run('open', { runId: 'receipt-two' }); p.run('open', { runId: 'receipt-two' });
    assert.equal(p.run('query', { runId: 'receipt-two' }).status, 'waiting');
    p.set(queryHtml, false); assert.equal(p.run('query', { runId: 'receipt-two' }).status, 'waiting'); assert.equal(p.clicks.query, 1);
    p.set(queryHtml, true); p.doc.querySelector('#subbutton').addEventListener('click', e => { e.preventDefault(); p.clicks.query++; });
    assert.equal(p.run('query', { runId: 'receipt-two' }).status, 'queried'); assert.equal(p.clicks.open, 2); assert.equal(p.clicks.query, 2);
});
