(request) => {
    'use strict';
    // Only identify returns naming metadata (company and last four digits); full account identity stays in this page.
    const result = (status, stage = 'unsupported', code) => code ? { status, stage, code } : { status, stage };
    const key = '__usbWebLauncherStatementExportV1';
    const root = window.document;
    const mainPath = '/NCCB/V6/b2bindex.jsp';
    // The account's bank layout chooses the numeric version (for example 08 or 09).
    const isBusinessPath = pathname => /^\/NCCB\/NECV6B2BMainPlat_\d+_(?:LH|FW)$/.test(pathname);
    let state;
    try {
        if (!request || typeof request.runId !== 'string' || !/^[\w-]{1,100}$/.test(request.runId)) return result('failed');
        const address = new URL(window.location.href);
        const trusted = url => url.protocol === 'https:' && !url.username && !url.password &&
            !url.port && ['b2b3.ccb.com', 'xc.b2b3.ccb.com'].includes(url.hostname) && url.origin === request.origin;
        const rootValid = () => {
            const now = new URL(window.location.href);
            return window.document === root && window === window.top && trusted(now) && now.pathname === mainPath;
        };
        if (!rootValid()) return result('unsupported');
        state = root[key];
        if (request.operation === 'dispose') {
            if (state && state.runId === request.runId) {
                state.account = null; state.startDate = null; state.endDate = null; state.naming = null;
                state.queryFrames = null; state.queryWindow = null; state.queryForm = null; state.queryDocument = null;
                state.menuAnchor = null; state.menuBox = null;
                state.homeDocument = null;
                state.disposed = true;
            }
            return result('disposed');
        }
        if (state && state.runId !== request.runId && !state.disposed) return result('failed', 'unsupported', 'run-conflict');
        const fresh = () => rootValid() && (request.expiresAt === undefined ||
            (Number.isFinite(request.expiresAt) && Date.now() < request.expiresAt)) &&
            root[key] === state && state && !state.disposed && state.runId === request.runId;
        const visible = node => {
            if (!node || !node.isConnected || !node.getClientRects().length) return false;
            for (let current = node; current; current = current.parentElement) {
                const css = current.ownerDocument.defaultView.getComputedStyle(current);
                if (current.hidden || css.display === 'none' || css.visibility === 'hidden' ||
                    css.visibility === 'collapse' || css.opacity === '0') return false;
            }
            return true;
        };
        const available = node => node && node.isConnected && !node.disabled && !node.hasAttribute('disabled') && node.getAttribute('aria-disabled') !== 'true';
        const enabled = node => available(node) && visible(node);
        const text = node => (node.textContent || '').replace(/\s+/g, '');
        const downloadLabel = node => {
            const copy = node.cloneNode(true);
            for (const menu of copy.querySelectorAll('ul,ol,[role="menu"]')) menu.remove();
            return text(copy);
        };
        const handler = (node, attribute, name) => {
            const value = node.getAttribute(attribute) || '';
            const match = /^\s*(?:return\s+)?([\w$.]+)\s*\(/.exec(value);
            return !!match && match[1] === name;
        };
        const list = (node, selector) => [...node.querySelectorAll(selector)];
        const one = (node, selector) => { const nodes = list(node, selector); return nodes.length === 1 ? nodes[0] : null; };
        const formNamed = (doc, name) => list(doc, 'form').filter(form => form.name === name || form.id === name);
        const bankForm = form => {
            const item = docs.find(item => item.doc === form.ownerDocument);
            if (!item) return false;
            const url = new URL(form.getAttribute('action') || '', item.baseUri);
            return trusted(url) && isBusinessPath(url.pathname);
        };
        const docs = [];
        const seen = new Set();
        const walk = (doc, chain, depth, inherited, inheritedBase) => {
            if (docs.length >= 16 || depth > 4 || seen.has(doc)) return;
            seen.add(doc);
            let url;
            try {
                url = new URL(doc.location.href);
                if (doc !== root && !(inherited && url.href === 'about:blank') &&
                    !(trusted(url) && isBusinessPath(url.pathname))) return;
            } catch (_) { return; }
            const current = () => {
                if (!rootValid()) return false;
                for (const pair of chain) {
                    if (!visible(pair.frame) || pair.frame.contentDocument !== pair.doc) return false;
                }
                const actual = new URL(doc.location.href);
                return doc === root || (inherited && actual.href === 'about:blank') || (trusted(actual) && isBusinessPath(actual.pathname));
            };
            const baseUri = doc.baseURI && doc.baseURI !== 'about:blank' ? doc.baseURI : inheritedBase;
            docs.push({ doc, current, chain, baseUri });
            if (depth === 4) return;
            for (const frame of list(doc, 'iframe,frame')) {
                if (docs.length >= 16) break;
                try {
                    const src = (frame.getAttribute('src') || '').trim();
                    const blank = !src || src === 'about:blank' || /^javascript:/i.test(src);
                    if (!blank) {
                        const declared = new URL(src, baseUri);
                        // The main frame retains b2bmainV7.jsp after navigating to LH.
                        // Its current document below determines the allowed business path.
                        if (!trusted(declared)) continue;
                    }
                    if (!visible(frame)) continue;
                    const child = frame.contentDocument;
                    if (child) walk(child, chain.concat({ frame, doc: child }), depth + 1, blank, baseUri);
                } catch (_) { /* Cross-origin or navigating frames are not candidates. */ }
            }
        };
        walk(root, [], 0, false, address.href);
        if (request.operation === 'ensure-home') {
            if (!state || state.runId !== request.runId) {
                state = { runId: request.runId, disposed: false, opened: false };
                root[key] = state;
            }
            if (!fresh() || state.opened) return result('failed', 'home', 'stale');
            // Match the bank's top navigation, never a results pager or a task close button.
            const homes = docs.flatMap(item => list(item.doc, '#menu1Content a[id="0000"]')
                .filter(node => enabled(node) && text(node) === '首页')
                .map(node => ({ ...item, node })));
            if (homes.length !== 1) return result(homes.length > 1 ? 'ambiguous' : 'unsupported', 'home', 'home-not-found');
            const home = homes[0];
            if (!home.current() || (state.homeDocument && state.homeDocument !== home.doc)) return result('failed', 'home', 'stale');
            const tabs = list(home.doc, '[id="task_0000"]');
            const frames = list(home.doc, 'iframe[id="win_0000"]');
            if (tabs.length > 1 || frames.length > 1) return result('ambiguous', 'home', 'home-not-found');
            const active = tabs.length === 1 && enabled(tabs[0]) && tabs[0].classList.contains('taskOn_div');
            if (active && frames.length === 1 && visible(frames[0]) && frames[0].getAttribute('isReady') === 'true') {
                const item = docs.find(item => item.doc === frames[0].contentDocument);
                if (item && item.current() && item.doc.readyState === 'complete') {
                    state.homeReady = true;
                    return result('home-ready', 'home');
                }
            }
            if (!active && !state.homeClicked) {
                if (!fresh() || !home.current() || !enabled(home.node)) return result('failed', 'home', 'stale');
                state.homeDocument = home.doc;
                state.homeClicked = true; // Consume before the original handler can reenter.
                home.node.click();
            }
            return result('waiting', 'home');
        }
        // The frameset root has no navigation; the bank renders this link in its LH main frame.
        // A collapsed submenu may hide the exact leaf while its owning frame remains active.
        const allEntries = docs.flatMap(item => list(item.doc, 'a')
            .filter(node => available(node) && text(node) === '活期账户' && handler(node, 'onclick', 'taskBar.createWin'))
            .map(node => ({ ...item, node })));
        const shownEntries = allEntries.filter(entry => visible(entry.node));
        const entries = shownEntries.length ? shownEntries : allEntries;
        const candidates = [];
        for (const item of docs) {
            for (const form of formNamed(item.doc, 'jhform')) {
                if (!visible(form) || !bankForm(form)) continue;
                let stage;
                if (list(form, 'input[name="ACC_NO"][type="checkbox"]').length) stage = 'accounts';
                else if (one(form, '[name="StDt"]') && one(form, '[name="EdDt"]')) stage = 'query';
                // Results can retain their query fields; sequence + print form identify that stage.
                if (formNamed(item.doc, 'elecPrintForm').length === 1 && (form.querySelector('[name="SEQ_NO"]') ||
                    (one(form, 'table[id="rsTable"].tableList') && one(form, 'table[id="headTable"]')))) stage = 'results';
                if (stage) candidates.push({ ...item, form, stage });
            }
        }
        const stage = candidates.length === 1 ? candidates[0].stage : entries.length === 1 ? 'home' : 'unsupported';
        if (request.operation === 'inspect') {
            if (candidates.length > 1 || (candidates.length === 0 && entries.length > 1)) return result('ambiguous', 'ambiguous');
            if (candidates.length === 1 && candidates[0].doc.readyState !== 'complete') return result('waiting', stage);
            return result(stage === 'unsupported' ? 'unsupported' : 'ready', stage);
        }
        if (request.operation === 'open-accounts') {
            if (state && state.runId === request.runId && (state.opened || state.disposed)) return result(state.disposed ? 'failed' : 'waiting', stage);
            if (state && !state.homeReady) return result('waiting', 'home');
            if (entries.length !== 1) return result(entries.length > 1 ? 'ambiguous' : 'unsupported', stage, entries.length === 0 ? 'entry-not-found' : undefined);
            state = { ...state, runId: request.runId, opened: false, disposed: false, selected: false, queried: false, downloaded: false, menu: false, account: null };
            root[key] = state;
            if (!fresh() || !entries[0].current() || !available(entries[0].node)) return result('failed', stage, 'stale');
            state.opened = true;
            entries[0].node.click();
            return result('opened', stage);
        }
        if (!fresh() || !state.opened) return result('failed', stage, 'stale');
        if (candidates.length > 1) return result('ambiguous', 'ambiguous');
        if (!candidates.length) return result('waiting', stage);
        const candidate = candidates[0];
        const doc = candidate.doc, form = candidate.form;
        // Date controls and result headers can precede account fields while HTML is parsing.
        if (doc.readyState !== 'complete') return result('waiting', stage);
        const canAct = node => fresh() && candidate.current() && form.isConnected && bankForm(form) &&
            node.ownerDocument === doc && enabled(node);
        const canSubmit = node => canAct(node) && node.form === form;
        if (request.operation === 'select-first') {
            if (state.selected) return result('waiting', stage);
            if (stage === 'query' || stage === 'results') {
                // taskBar.createWin already submitted the account-list navigation.
                // BacktoFirst on the old document can replace that request with a
                // query page and leave this run waiting forever for its first selection.
                return result('waiting', stage);
            }
            if (stage !== 'accounts') return result('unsupported', stage);
            const rows = list(form, 'input[name="ACC_NO"][type="checkbox"]');
            const buttons = list(form, 'input[name="Submit3"][type="button"]').filter(node => enabled(node) && handler(node, 'onclick', 'detailQuery'));
            if (buttons.length !== 1) return result(buttons.length > 1 ? 'ambiguous' : 'unsupported', stage);
            if (!rows.length || !enabled(rows[0]) || !rows[0].value || rows.some(node => node.disabled)) return result('unsupported', stage);
            if (!canSubmit(buttons[0]) || !canSubmit(rows[0]) || rows.some(node => node.form !== form)) return result('failed', stage, 'stale');
            state.account = rows[0].value;
            for (const node of list(form, 'input[type="checkbox"]')) {
                if (node.name === 'ACC_NO' || ['all_0', 'all_1'].includes(node.name) || ['all_0', 'all_1'].includes(node.id)) node.checked = node === rows[0];
            }
            if (!canSubmit(buttons[0]) || rows[0].value !== state.account) return result('failed', stage, 'stale');
            state.selected = true; buttons[0].click();
            return result('selected', stage);
        }
        if (request.operation === 'query') {
            if (!state.selected) return result('failed', stage, 'missing-selection');
            if (state.queryAttempted) return result('waiting', stage);
            if (stage !== 'query') return result('waiting', stage);
            const account = one(form, 'input[name="ACC_NO"][type="hidden"]');
            if (!account || account.value !== state.account) return result('failed', stage, 'account-mismatch');
            const start = one(form, 'input[name="StDt"]'), end = one(form, 'input[name="EdDt"]');
            const submit = one(form, 'input[name="Submit4"][type="submit"]');
            const validDate = value => {
                if (typeof value !== 'string' || !/^\d{8}$/.test(value)) return false;
                const date = new Date(value.slice(0, 4) + '-' + value.slice(4, 6) + '-' + value.slice(6, 8) + 'T00:00:00Z');
                return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10).replace(/-/g, '') === value;
            };
            if (!validDate(request.startDate) || !validDate(request.endDate) || request.startDate > request.endDate) return result('failed', stage, 'invalid-date');
            if (!submit || !start || !end || !handler(form, 'onsubmit', 'go') || !canSubmit(submit) || !canSubmit(start) || !canSubmit(end) || account.form !== form || start.readOnly || end.readOnly) return result('unsupported', stage);
            // Consume before dispatching bank events, which can synchronously reenter this adapter.
            state.queryAttempted = true;
            start.value = request.startDate; end.value = request.endDate;
            for (const node of [start, end]) {
                node.dispatchEvent(new doc.defaultView.Event('input', { bubbles: true }));
                node.dispatchEvent(new doc.defaultView.Event('change', { bubbles: true }));
            }
            if (!canSubmit(submit) || !handler(form, 'onsubmit', 'go') || account.form !== form || account.value !== state.account || start.value !== request.startDate || end.value !== request.endDate) return result('failed', stage, 'stale');
            state.startDate = request.startDate; state.endDate = request.endDate;
            state.queryFrames = candidate.chain.map(pair => pair.frame);
            state.queryWindow = doc.defaultView;
            state.queryForm = form; state.queryDocument = doc;
            state.queried = true; submit.click();
            return result('queried', stage);
        }
        if (request.operation === 'identify' || request.operation === 'download') {
            if (state.menuDispatching) return result('waiting', stage);
            if (!state.queried) return result('failed', stage, 'missing-query');
            if (state.downloaded) return result('waiting', stage);
            if (stage !== 'results') return result('waiting', stage);
            // A result header can arrive before its rows or identity fields finish parsing.
            if (!list(doc, '[name="SEQ_NO"]').length && doc.readyState !== 'complete') return result('waiting', stage);
            const printForm = formNamed(doc, 'elecPrintForm')[0];
            if (!bankForm(printForm)) return result('unsupported', stage);
            const sameLineage = state.queryFrames && state.queryFrames.length === candidate.chain.length &&
                state.queryFrames.every((frame, index) => frame === candidate.chain[index].frame);
            if (!sameLineage || (!candidate.chain.length && state.queryWindow !== doc.defaultView)) return result('failed', stage, 'result-lineage');
            const queryIdentityMatches = () => {
                for (const [name, expected] of [['ACC_NO', state.account], ['StDt', state.startDate], ['EdDt', state.endDate]]) {
                    const fields = [form, printForm].flatMap(owner => list(owner, `input[name="${name}"]`));
                    if (!fields.length || fields.some(field => ![form, printForm].includes(field.form) || field.value !== expected)) return false;
                }
                return true;
            };
            if (!queryIdentityMatches()) return result('failed', stage, 'result-identity');
            const readNaming = () => {
                const table = one(form, 'table[id="headTable"]');
                if (!table || !canAct(table)) return null;
                const labels = list(table, 'td,th');
                const accountLabels = labels.filter(node => text(node).replace(/[:：]$/, '') === '账号');
                const companyLabels = labels.filter(node => text(node).replace(/[:：]$/, '') === '账户名称');
                if (accountLabels.length !== 1 || companyLabels.length !== 1 ||
                    accountLabels[0].parentElement !== companyLabels[0].parentElement) return null;
                const accountCell = accountLabels[0].nextElementSibling, companyCell = companyLabels[0].nextElementSibling;
                if (!accountCell || !companyCell || accountCell.tagName !== 'TD' || companyCell.tagName !== 'TD' ||
                    ![accountLabels[0], companyLabels[0], accountCell, companyCell].every(canAct)) return null;
                const account = accountCell.textContent.trim(), companyName = companyCell.textContent.trim();
                if (!/^\d{4,40}$/.test(account) || !companyName || companyName.length > 225 ||
                    /[\u0000-\u001f\u007f\u2026\ufffd]|\.{3}/.test(companyName)) return null;
                const accountField = one(form, 'input[name="checkedAccno"][type="hidden"]');
                const companyField = one(form, 'input[name="checkedAccName"][type="hidden"]');
                const selected = state.account.split('@');
                if (!accountField || !companyField || accountField.form !== form || companyField.form !== form ||
                    accountField.value !== account || companyField.value !== companyName ||
                    selected[0] !== account || selected[1] !== companyName) return null;
                return { companyName, accountLast4: account.slice(-4) };
            };
            const namingMatches = () => {
                if (!state.naming) return true;
                const current = readNaming();
                return current && current.companyName === state.naming.companyName && current.accountLast4 === state.naming.accountLast4;
            };
            if (!namingMatches()) return result('failed', stage, 'naming-identity');
            if (!list(doc, '[name="SEQ_NO"]').length) {
                // Derived zero-row case of the bank's verified rsTable schema. Missing DOM,
                // an error row, or arbitrary "no records" prose is never treated as empty.
                const table = one(form, 'table[id="rsTable"].tableList');
                const rows = table ? list(table, 'tr') : [];
                const newResult = state.queryForm && form !== state.queryForm &&
                    (doc !== state.queryDocument || !state.queryForm.isConnected);
                const noLogin = !docs.some(item => list(item.doc, 'input[type="password"],#LOG_PWD,#LOGIN_BOX_BTN').some(visible));
                const empty = doc.readyState === 'complete' && newResult && noLogin && table && canAct(table) &&
                    rows.length === 2 && rows.every((row, index) => row.closest('table') === table && canAct(row) &&
                        row.classList.contains('tableListHead') && row.cells.length === (index === 0 ? 16 : 2) &&
                        [...row.cells].every(cell => cell.tagName === 'TD' && canAct(cell)));
                if (!empty) return result('failed', stage, 'empty-result-unconfirmed');
                if (!readNaming()) return result('failed', stage, 'naming-identity');
                return result('empty', stage);
            }
            if (request.operation === 'identify') {
                const naming = readNaming();
                if (!naming) return result('failed', stage, 'naming-identity');
                state.naming = naming;
                return { status: 'identified', stage, companyName: naming.companyName, accountLast4: naming.accountLast4 };
            }
            const identityMatches = () => queryIdentityMatches() && namingMatches();
            const anchors = list(doc, 'a').filter(node => enabled(node) && ['dlall', 'dlAll'].includes(node.id) && downloadLabel(node) === '下载全部');
            if (!anchors.length) return result('unsupported', stage, 'download-owner');
            // Top and bottom toolbars can repeat the all-download action. Each owner is proved
            // independently; a retry remains bound to the owner whose menu was first opened.
            const resolveMenu = anchor => {
            const menus = new Set();
            const isMenu = node => node && (['UL', 'OL'].includes(node.tagName) || node.getAttribute('role') === 'menu');
            for (const node of list(anchor, 'ul,ol,[role="menu"]')) menus.add(node);
            if (isMenu(anchor.nextElementSibling)) menus.add(anchor.nextElementSibling);
            for (const attr of ['aria-controls', 'aria-owns']) {
                for (const id of (anchor.getAttribute(attr) || '').split(/\s+/).filter(Boolean)) {
                    const nodes = list(doc, '[id]').filter(node => node.id === id);
                    if (nodes.length !== 1) return { error: result('ambiguous', stage, 'download-menu') };
                    menus.add(nodes[0]);
                }
            }
            if (!menus.size) return { error: result('unsupported', stage, 'download-menu') };
            const pdfs = new Set();
            for (const menu of menus) {
                // Reject a menu also explicitly controlled by any other control.
                if (menu.id && list(doc, '[aria-controls],[aria-owns]').some(node => node !== anchor &&
                    ['aria-controls', 'aria-owns'].some(attr => (node.getAttribute(attr) || '').split(/\s+/).includes(menu.id)))) return { error: result('ambiguous', stage, 'download-menu') };
                // A sibling menu owned by another download control cannot be borrowed via ARIA.
                const previous = menu.previousElementSibling;
                if (!anchor.contains(menu) && previous && previous !== anchor &&
                    (previous.matches('a.download,a[id="dlall"],a[id="dlAll"]') ||
                        (previous.tagName === 'A' && /下载/.test(downloadLabel(previous))))) return { error: result('ambiguous', stage, 'download-menu') };
                // A PDF nested under another control is not evidence of this owner's action.
                if (list(menu, 'a').some(node => /下载/.test(downloadLabel(node)))) return { error: result('ambiguous', stage, 'download-menu') };
                for (const item of list(menu, 'li')) {
                    if (/^PDF(?:下载)?$/i.test(text(item)) && handler(item, 'onclick', 'submitSelect')) pdfs.add(item);
                }
            }
            if (pdfs.size !== 1) return { error: result(pdfs.size > 1 ? 'ambiguous' : 'unsupported', stage, 'download-pdf') };
            return { anchor, item: [...pdfs][0] };
            };
            const owners = anchors.map(resolveMenu);
            // Ambiguous ownership is never bypassed by selecting another toolbar.
            const ambiguity = owners.find(owner => owner.error && owner.error.status === 'ambiguous');
            if (ambiguity) return ambiguity.error;
            const proven = owners.filter(owner => !owner.error);
            const chosen = state.menuAnchor ? proven.find(owner => owner.anchor === state.menuAnchor) : proven[0];
            if (!chosen) {
                // The bank's ccbDownload plugin appends disconnected menus to BODY and
                // closes over the owner in its mouseover handler. Prove that association
                // by a hidden-to-visible transition, never by global PDF label order.
                if (!owners.every(owner => owner.error && owner.error.status === 'unsupported' && owner.error.code === 'download-menu'))
                    return state.menuAnchor ? result('failed', stage, 'stale') : owners[0].error;
                const anchor = state.menuAnchor || anchors[0];
                if (!anchors.includes(anchor) || !canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                const boxes = list(doc, 'body > div.downloadBox');
                if (!boxes.length) return result('unsupported', stage, 'download-menu');
                const shown = boxes.filter(visible);
                state.menuAnchor = anchor;
                state.menuDispatching = true;
                try {
                    if (shown.length) {
                        if (state.menuReset) return result('unsupported', stage, 'download-menu');
                        state.menuReset = true;
                        for (const box of shown) {
                            if (!canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                            // jQuery implements its mouseleave binding through mouseout.
                            // Leaving to no related target invokes the original delayed hide.
                            box.dispatchEvent(new doc.defaultView.MouseEvent('mouseout', { bubbles: true, relatedTarget: null }));
                        }
                        if (!canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                        return result('waiting', stage);
                    }
                    if (state.menuHovered) return result('unsupported', stage, 'download-menu');
                    state.menuHovered = true;
                    anchor.dispatchEvent(new doc.defaultView.MouseEvent('mouseover', { bubbles: true }));
                    if (!canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                    const revealed = list(doc, 'body > div.downloadBox').filter(visible);
                    if (revealed.length !== 1 || !boxes.includes(revealed[0])) return result(revealed.length > 1 ? 'ambiguous' : 'unsupported', stage, 'download-menu');
                    const box = revealed[0];
                    const children = [...box.children];
                    const header = children[0], menu = children[1];
                    if (children.length !== 2 || !header || header.tagName !== 'SPAN' || header.children.length !== 1 ||
                        header.firstElementChild.tagName !== 'A' || text(header) !== '下载全部' || !menu || menu.tagName !== 'UL' ||
                        list(menu, 'a').some(node => /下载/.test(downloadLabel(node)))) return result('unsupported', stage, 'download-menu');
                    const pdfs = list(menu, 'li').filter(item => item.parentElement === menu && /^PDF(?:下载)?$/i.test(text(item)) && handler(item, 'onclick', 'submitSelect'));
                    if (pdfs.length !== 1) return result(pdfs.length > 1 ? 'ambiguous' : 'unsupported', stage, 'download-pdf');
                    const item = pdfs[0];
                    if (!canAct(item) || !canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                    state.menuBox = box;
                    state.downloaded = true;
                    item.click();
                    return result('downloading', stage);
                } finally { state.menuDispatching = false; }
            }
            if (proven.some(owner => owner !== chosen && owner.item === chosen.item)) return result('ambiguous', stage, 'download-menu');
            const { anchor, item } = chosen;
            if (!enabled(item)) {
                if (!state.menu) {
                    if (!canAct(anchor) || !identityMatches()) return result('failed', stage, 'stale');
                    state.menu = true; state.menuAnchor = anchor; anchor.click();
                }
                return result('waiting', stage);
            }
            if (!canAct(item) || !identityMatches()) return result('failed', stage, 'stale');
            state.downloaded = true; item.click();
            return result('downloading', stage);
        }
        return result('unsupported', stage);
    } catch (_) { return result('failed'); }
}
