const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const source = fs.readFileSync(path.join(__dirname, '../../src/UsbWebLauncher.App/Browser/CcbStatementDiagnostics.js'), 'utf8');
function page(html = '', url = 'https://b2b3.ccb.com/NCCB/home?token=PRIVATE_TOKEN') {
    const dom = new JSDOM(html, { url, runScripts: 'outside-only' });
    return { w: dom.window, run: () => JSON.parse(JSON.stringify(dom.window.eval(`(${source})()`))) };
}
test('captures receipt workflow markers and control relationships without private values', () => {
    const p = page(`<a>首页</a><a>账户查询</a><a>电子回单查询</a><form id="receiptQuery">
      <label for="queryKind">查询类型</label><select id="queryKind"><option>批量回单查询</option></select>
      <div><span>起止日期</span><input id="start"><input id="end"></div>
      <label for="printState">打印状态</label><select id="printState"><option>全部</option></select>
      <span>文件格式</span><label><input id="pdf" type="radio" checked>PDF</label>
      <span>合并下载</span><label><input id="merge" type="radio" checked>是</label>
      <button onclick="receipt.query('PRIVATE_ARGUMENT')">确定</button></form>
      <h3>批量回单查询结果</h3><input type="checkbox" id="row" value="PRIVATE_RECEIPT">
      <label for="selectAll">全选</label><input id="selectAll" type="checkbox" checked onclick="receipt.selectAll('PRIVATE_ARGUMENT')">
      <a onclick="receipt.preview('PRIVATE_ARGUMENT')">电子回单预览/打印</a>
      <div id="pager"><a>首页</a><a>上一页</a><input name="pageIndex" value="PRIVATE_PAGE"><a>下一页</a><span>共2页</span><span>到第</span><a>转至</a></div>`);
    for (const field of p.w.document.querySelectorAll('input,select,option')) {
        Object.defineProperty(field, 'value', { get() { throw Error('must not read field values'); } });
    }
    const doc = p.run().documents[0];
    for (const expected of ['home', 'receipt-query', 'query-type', 'date-range', 'print-status', 'file-format', 'merge-download', 'receipt-results', 'select-all', 'receipt-preview-print', 'previous-page', 'next-page', 'page-jump']) {
        assert.ok(doc.markers.includes(expected), expected);
    }
    const selectLabel = doc.controls.find(c => c.markers.includes('select-all'));
    assert.equal(selectLabel.labelFor, 'selectAll');
    const all = doc.fields.find(f => f.id === 'selectAll');
    assert.equal(all.checked, true);
    assert.equal(doc.fields.find(f => f.id === 'row').checked, false);
    assert.equal(doc.fields.find(f => f.id === 'pdf').checked, true);
    assert.equal(doc.fields.find(f => f.id === 'start').ancestorRefs[0], doc.fields.find(f => f.id === 'end').ancestorRefs[0]);
    const dateLabel = doc.controls.find(c => c.markers.includes('date-range'));
    assert.equal(dateLabel.ancestorRefs[0], doc.fields.find(f => f.id === 'start').ancestorRefs[0]);
    assert.equal(doc.controls.find(c => c.markers.includes('receipt-preview-print')).handlers.click, 'receipt.preview');
    assert.doesNotMatch(JSON.stringify(doc), /PRIVATE_|共2页|2026/);
    p.w.close();
});

test('reports controls and fields truncation so incomplete receipt selection evidence is explicit', () => {
    const p = page('<input type="checkbox" name="receipt">'.repeat(81) + '<label for="all">全选</label>');
    const doc = p.run().documents[0];
    assert.equal(doc.controlsTruncated, true);
    assert.equal(doc.fieldsTruncated, true);
    assert.equal(doc.truncated, true);
    assert.equal(doc.counts.checkbox, 81);
    assert.ok(doc.controls.some(c => c.markers.includes('select-all')));
    assert.equal(doc.fields.length, 80);
    p.w.close();
});
test('reports workflow controls and form schema without executing or mutating', () => {
    const p = page(`<a id="accountMenu" onclick="return menu.open('PRIVATE_ARG')">账户查询</a><a>账户信息查询</a><a>活期账户</a><button>交易明细</button><form id="queryForm" method="post" action="/NCCB/query?ticket=PRIVATE_TICKET"><input name="beginDate" value="20260901"><input name="endDate" value="20260930"><input type="checkbox" name="account" value="123456789012"><select name="format"><option value="PRIVATE_OPTION">PDF</option></select><button onclick="query.submit('PRIVATE_ARG')">确定</button></form><a>下载全部</a><a onclick="downloadPdf('PRIVATE_ARG')">PDF下载</a>`);
    const before = p.w.document.documentElement.outerHTML;
    let clicked = 0;
    p.w.document.addEventListener('click', () => clicked++);
    for (const name of ['MutationObserver', 'setInterval', 'setTimeout', 'fetch', 'XMLHttpRequest']) p.w[name] = () => { throw Error(name); };
    const result = p.run(), doc = result.documents[0];
    assert.equal(result.status, 'diagnostic');
    assert.deepEqual(doc.markers, ['account-query', 'account-info', 'current-account', 'transaction-detail', 'confirm', 'download-all', 'pdf-download']);
    assert.equal(doc.controls.find(c => c.id === 'accountMenu').handlers.click, 'menu.open');
    assert.equal(doc.forms[0].method, 'post');
    assert.deepEqual(doc.forms[0].action, { origin: 'https://b2b3.ccb.com', path: '/NCCB/query', queryNames: ['ticket'] });
    assert.equal(doc.fields.find(f => f.name === 'format').optionCount, 1);
    assert.equal(doc.counts.checkbox, 1);
    assert.equal(clicked, 0);
    assert.equal(p.w.document.documentElement.outerHTML, before);
    assert.doesNotMatch(JSON.stringify(result), /PRIVATE_|202609|123456789012/);
    p.w.close();
});
test('never reads credential properties or exports arbitrary body text', () => {
    const p = page('<p>PRIVATE_ACCOUNT</p><input type="password" name="pin" value="PRIVATE_PIN"><input name="date"><button title="PRIVATE_TITLE" onclick="alert(\'PRIVATE_MESSAGE\')">确定 PRIVATE_TEXT</button>');
    for (const [obj, prop] of [[p.w.HTMLInputElement.prototype, 'value'], [p.w.document, 'cookie'], [p.w, 'localStorage'], [p.w, 'sessionStorage'], [p.w.Element.prototype, 'innerHTML'], [p.w.Element.prototype, 'outerHTML']]) Object.defineProperty(obj, prop, { get() { throw Error(prop); } });
    assert.doesNotMatch(JSON.stringify(p.run()), /PRIVATE_|TOKEN/);
    p.w.close();
});
test('sanitizes encoded identifiers, paths, parameters and handler names', () => {
    const p = page('<form id="account_123456789012" action="/NCCB/acct/%31%32%33%34%35%36%37%38?1234567890=SECRET&session=SECRET"><input name="field_abcdef123456abcdef123456"><button id="%2531%2532%2533%2534%2535%2536" onclick="bank.secret123456789012()">确定</button></form><script src="/NCCB/static/main.js?token=SECRET"></script><script src="https://outside.example/PRIVATE_SCRIPT"></script>');
    const result = p.run();
    assert.doesNotMatch(JSON.stringify(result), /123456|abcdef123456|%25|%31|SECRET|PRIVATE_SCRIPT|outside.example/);
    assert.equal(result.documents[0].scripts[0].path, '/NCCB/static/main.js');
    p.w.close();
});
test('extracts handler names only, ignoring scripts inside strings and arbitrary expressions', () => {
    const p = page(`<button onclick="return bank.query('secretFunction(1234567890)')">确定</button><button onclick="'PRIVATE_STRING';dangerous()">交易明细</button><a href="javascript:download.pdf('PRIVATE_ARGUMENT')">PDF下载</a>`);
    const controls = p.run().documents[0].controls;
    assert.equal(controls[0].handlers.click, 'bank.query');
    assert.deepEqual(controls[1].handlers, {});
    assert.equal(controls[2].handlers.href, 'download.pdf');
    assert.doesNotMatch(JSON.stringify(controls), /PRIVATE_|secretFunction|dangerous/);
    p.w.close();
});
test('restricts root document to bank HTTPS standard port and NCCB path', () => {
    for (const url of ['http://ccb.com/NCCB/', 'https://ccb.com:444/NCCB/', 'https://ccb.com.evil.test/NCCB/', 'https://evilccb.com/NCCB/', 'https://ccb.com/outside']) {
        const p = page('<button>确定</button>', url);
        assert.deepEqual(p.run(), { status: 'unsupported', documents: [] }); p.w.close();
    }
    const p = page('', 'https://ccb.com:443/NCCB'); assert.equal(p.run().status, 'diagnostic'); p.w.close();
});
test('does not inspect declared cross-origin frames or redirected outside documents', () => {
    const p = page('<iframe src="https://other.ccb.com/NCCB/page"></iframe><iframe src="/NCCB/child"></iframe>');
    const frames = p.w.document.querySelectorAll('iframe'); let crossRead = false;
    Object.defineProperty(frames[0], 'contentDocument', { get() { crossRead = true; throw Error('cross origin'); } });
    const child = page('<button>确定</button>', 'https://b2b3.ccb.com/outside');
    Object.defineProperty(frames[1], 'contentDocument', { get: () => child.w.document });
    assert.equal(p.run().documents.length, 1); assert.equal(crossRead, false); p.w.close(); child.w.close();
});
test('accepts safe actual documents in legacy javascript and inherited blank frames', () => {
    const p = page('<iframe></iframe><iframe></iframe>');
    const safe = page('<button>交易明细</button>', 'https://b2b3.ccb.com/NCCB/detail');
    const frames = p.w.document.querySelectorAll('iframe');
    const get = frames[0].getAttribute.bind(frames[0]);
    frames[0].getAttribute = name => name === 'src' ? 'javascript:PRIVATE_SOURCE()' : get(name);
    Object.defineProperty(frames[0], 'contentDocument', { get: () => safe.w.document });
    frames[1].contentDocument.body.innerHTML = '<button>PDF下载</button>';
    const result = p.run(); assert.equal(result.documents.length, 3);
    assert.ok(result.documents[1].markers.includes('transaction-detail'));
    assert.ok(result.documents[2].markers.includes('pdf-download'));
    assert.doesNotMatch(JSON.stringify(result), /PRIVATE_SOURCE/);
    p.w.close(); safe.w.close();
});
test('deduplicates markers and document references and bounds fields and nodes', () => {
    const p = page('<button>确定</button>'.repeat(6500) + '<input name="beyondLimit"><iframe></iframe>');
    const doc = p.run().documents[0];
    assert.deepEqual(doc.markers, ['confirm']); assert.ok(doc.controls.length <= 60);
    assert.ok(doc.inspectedNodes <= 6000); assert.equal(doc.fields.length, 0); p.w.close();
    const q = page('<iframe></iframe><iframe></iframe>'), child = page('<input name="date">');
    for (const f of q.w.document.querySelectorAll('iframe')) Object.defineProperty(f, 'contentDocument', { get: () => child.w.document });
    assert.equal(q.run().documents.length, 2); q.w.close(); child.w.close();
});
test('bounds child depth to four and documents to sixteen', () => {
    const chain = Array.from({ length: 7 }, () => page('<iframe></iframe>'));
    for (let i = 0; i < 6; i++) Object.defineProperty(chain[i].w.document.querySelector('iframe'), 'contentDocument', { get: () => chain[i + 1].w.document });
    assert.equal(chain[0].run().documents.length, 5); for (const p of chain) p.w.close();
    const p = page('<iframe></iframe>'.repeat(25));
    assert.equal(p.run().documents.length, 16); p.w.close();
});
test('retains workflow targets after large navigation menus and deduplicates scripts', () => {
    const p = page('<a class="navigation">菜单</a>'.repeat(100) + '<button id="submitQuery" onclick="query.submit()">确定</button><a id="pdf" onclick="exportPdf()">PDF下载</a><script src="/NCCB/app.js?ticket=ONE"></script><script src="/NCCB/app.js?ticket=TWO"></script>');
    const doc = p.run().documents[0];
    assert.equal(doc.controls.length, 60);
    assert.ok(doc.controls.some(c => c.id === 'submitQuery'));
    assert.ok(doc.controls.some(c => c.id === 'pdf'));
    assert.equal(doc.scripts.length, 1); p.w.close();
});
test('rejects actual cross-origin legacy frame content and blank redirects from normal declared URLs', () => {
    const p = page('<iframe></iframe><iframe src="/NCCB/child"></iframe>');
    const outside = page('<button>确定</button>', 'https://other.ccb.com/NCCB/child');
    const blank = page('<button>PDF下载</button>', 'about:blank');
    const frames = p.w.document.querySelectorAll('iframe');
    Object.defineProperty(frames[0], 'contentDocument', { get: () => outside.w.document });
    Object.defineProperty(frames[1], 'contentDocument', { get: () => blank.w.document });
    assert.equal(p.run().documents.length, 1); p.w.close(); outside.w.close(); blank.w.close();
});
test('captures actionable ancestors and dollar function names for nested workflow labels', () => {
    const p = page('<a id="pdfMenu" onclick="$.exportPdf(\'PRIVATE_ARG\')"><span><b>PDF下载</b></span></a>');
    const target = p.run().documents[0].controls.find(c => c.markers.includes('pdf-download'));
    assert.ok(target.ancestors.some(a => a.id === 'pdfMenu' && a.handlers.click === '$.exportPdf'));
    p.w.close();
});
test('finds child frames independently of oversized menu traversal', () => {
    const p = page('<div>菜单</div>'.repeat(6200) + '<iframe></iframe>');
    p.w.document.querySelector('iframe').contentDocument.body.innerHTML = '<button>交易明细</button>';
    assert.equal(p.run().documents.length, 2); p.w.close();
});
test('redacts UUID and opaque token identifiers and caps serialized snapshot size', () => {
    const p = page('<button id="550e8400-e29b-41d4-a716-446655440000">确定</button><input name="ZxT8pQ2fUvLmN7rWaS4bY6cD9eG1hJ5k"><iframe></iframe>'.repeat(18));
    for (const frame of p.w.document.querySelectorAll('iframe')) frame.contentDocument.body.innerHTML = '<form id="queryForm"><input name="beginDate"><button onclick="bank.query()">确定</button></form>'.repeat(90);
    const serialized = JSON.stringify(p.run());
    assert.ok(serialized.length <= 96 * 1024);
    assert.doesNotMatch(serialized, /550e8400-e29b|ZxT8pQ2fUvLmN7rWaS4bY6cD9eG1hJ5k/);
    p.w.close();
});
test('payload trimming retains workflow controls in late documents with maximal metadata', () => {
    const longClass = ['longTechnicalStyleAlpha', 'longTechnicalStyleBravo', 'longTechnicalStyleCharlie', 'longTechnicalStyleDelta', 'longTechnicalStyleEcho', 'longTechnicalStyleFoxtrot'].join(' ');
    const html = `<form id="statementQueryForm" class="${longClass}" action="/NCCB/query?beginDate=SECRET&endDate=SECRET">` + `<input name="beginDate" class="${longClass}" onchange="bank.verifyField()">`.repeat(80) + `<a id="pdfDownloadControl" class="${longClass}" onclick="$.exportPdf()"><span>PDF下载</span></a></form>`;
    const p = page(html + '<iframe></iframe>'.repeat(15));
    for (const frame of p.w.document.querySelectorAll('iframe')) frame.contentDocument.body.innerHTML = html;
    const result = p.run();
    assert.equal(result.documents.length, 16);
    assert.ok(JSON.stringify(result).length <= 96 * 1024);
    for (const doc of result.documents) {
        assert.ok(doc.controls.some(control => control.markers.includes('pdf-download')));
        assert.ok(doc.forms.length > 0);
    }
    p.w.close();
});
test('resolves relative form, script and frame URLs inside inherited blank documents', () => {
    const p = page('<iframe></iframe>');
    const child = p.w.document.querySelector('iframe').contentDocument;
    child.body.innerHTML = '<form action="query?ticket=PRIVATE_TOKEN"><input name="date"></form><script src="app.js"></script><iframe src="child"></iframe>';
    const grandchild = page('<button>PDF下载</button>', 'https://b2b3.ccb.com/NCCB/child');
    Object.defineProperty(child.querySelector('iframe'), 'contentDocument', { get: () => grandchild.w.document });
    const result = p.run();
    assert.equal(result.documents.length, 3);
    assert.equal(result.documents[1].forms[0].action.path, '/NCCB/query');
    assert.equal(result.documents[1].scripts[0].path, '/NCCB/app.js');
    assert.equal(result.documents[1].frames[0].address.path, '/NCCB/child');
    p.w.close(); grandchild.w.close();
});
