(request) => {
    'use strict';
    // Observed bank controls only. Full account/option values remain in page-local state.
    const result = (status, stage = 'unsupported', code, diagnostics) => {
        const output = code ? { status, stage, code } : { status, stage };
        if (diagnostics) output.diagnostics = diagnostics;
        return output;
    };
    const key = '__usbWebLauncherReceiptExportV1', root = window.document;
    // The account's bank layout chooses the numeric version (for example 08 or 09).
    const isBusinessPath = pathname => /^\/NCCB\/NECV6B2BMainPlat_\d+_(?:LH|FW)$/.test(pathname);
    let state;
    try {
        if (!request || typeof request.runId !== 'string' || !/^[\w-]{1,100}$/.test(request.runId)) return result('failed');
        const trusted = url => url.protocol === 'https:' && !url.username && !url.password && !url.port &&
            ['b2b3.ccb.com', 'xc.b2b3.ccb.com'].includes(url.hostname) && url.origin === request.origin;
        const rootValid = () => window === window.top && window.document === root && trusted(new URL(window.location.href)) &&
            new URL(window.location.href).pathname === '/NCCB/V6/b2bindex.jsp';
        if (!rootValid()) return result('unsupported');
        state = root[key];
        if (request.operation === 'dispose') {
            if (state && state.runId === request.runId) {
                for (const property of ['account', 'company', 'optionValue', 'startDate', 'endDate', 'homeDocument', 'homeScope', 'queryDocument', 'queryChain', 'resultDocument', 'naming', 'oldDocuments', 'expectedFilters']) state[property] = null;
                state.disposed = true;
            }
            return result('disposed');
        }
        if (state && state.runId !== request.runId && !state.disposed) return result('failed', 'unsupported', 'run-conflict');
        if ((!state || state.runId !== request.runId) && request.operation === 'ensure-home') {
            state = { runId: request.runId, disposed: false };
            root[key] = state;
        }
        const fresh = () => rootValid() && state && root[key] === state && !state.disposed && state.runId === request.runId &&
            Number.isFinite(request.expiresAt) && Date.now() < request.expiresAt;
        if (!fresh()) return result('failed', 'unsupported', 'stale');
        const list = (node, selector) => [...node.querySelectorAll(selector)];
        const one = (node, selector) => { const nodes = list(node, selector); return nodes.length === 1 ? nodes[0] : null; };
        const text = node => (node.textContent || '').replace(/\s+/g, '');
        const handler = (node, attribute, name) => {
            const match = /^\s*(?:javascript:\s*)?(?:return\s+)?([\w$.]+)\s*\(/.exec(node.getAttribute(attribute) || '');
            return !!match && match[1] === name;
        };
        const visible = node => {
            if (!node || !node.isConnected || !node.getClientRects().length) return false;
            for (let current = node; current; current = current.parentElement) {
                const css = current.ownerDocument.defaultView.getComputedStyle(current);
                if (current.hidden || css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0') return false;
            }
            return true;
        };
        const available = node => node && node.isConnected && !node.disabled && !node.hasAttribute('disabled') && node.getAttribute('aria-disabled') !== 'true';
        const enabled = node => available(node) && visible(node);
        const docs = [], seen = new Set();
        let exhausted = false;
        const walk = (doc, chain, depth, inherited, inheritedBase) => {
            if (seen.has(doc)) return;
            if (depth > 4 || docs.length >= 16) { exhausted = true; return; }
            let url;
            try {
                url = new URL(doc.location.href);
                if (doc !== root && !(inherited && url.href === 'about:blank') && !(trusted(url) && isBusinessPath(url.pathname))) return;
            } catch (_) { return; }
            seen.add(doc);
            const current = () => {
                if (!rootValid()) return false;
                for (const pair of chain) if (!visible(pair.frame) || pair.frame.contentDocument !== pair.doc) return false;
                const actual = new URL(doc.location.href);
                return doc === root || (inherited && actual.href === 'about:blank') || (trusted(actual) && isBusinessPath(actual.pathname));
            };
            const baseUri = doc.baseURI && doc.baseURI !== 'about:blank' ? doc.baseURI : inheritedBase;
            docs.push({ doc, current, chain, baseUri });
            for (const frame of list(doc, 'iframe,frame')) {
                try {
                    if (!visible(frame)) continue;
                    const src = (frame.getAttribute('src') || '').trim();
                    const blank = !src || src === 'about:blank' || /^javascript:/i.test(src);
                    if (!blank && !trusted(new URL(src, baseUri))) continue;
                    if (frame.contentDocument) walk(frame.contentDocument, chain.concat({ frame, doc: frame.contentDocument }), depth + 1, blank, baseUri);
                } catch (_) { }
            }
        };
        walk(root, [], 0, false, window.location.href);
        if (exhausted) return result('ambiguous', 'unsupported', 'document-limit');
        if (docs.some(item => list(item.doc, 'input[type="password"],#LOG_PWD,#LOGIN_BOX_BTN').some(visible))) return result('failed', 'home', 'login-required');
        const identity = scope => {
            const companyNode = one(scope, '#ACCNM_ZHGS.deptName'), accountNode = one(scope, '#ACCNO_ZHGS.accNo');
            if (!companyNode || !accountNode) return null;
            const company = (companyNode.textContent || '').trim(), account = (accountNode.textContent || '').trim();
            if (!/^\d{4,40}$/.test(account) || !company || company.length > 225 || /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff]|…|\.{3}/u.test(company)) return null;
            return { company, account };
        };
        const originalIdentity = () => state.homeScope && state.homeScope.isConnected &&
            identity(state.homeScope)?.account === state.account && identity(state.homeScope)?.company === state.company;
        if (request.operation === 'ensure-home') {
            if (state.opened) return result('failed', 'home', 'stale');
            const homes = docs.flatMap(item => list(item.doc, '#menu1Content a[id="0000"]')
                .filter(node => enabled(node) && text(node) === '首页' && handler(node, 'onclick', 'taskBar.createWin')).map(node => ({ ...item, node })));
            const homeDiagnostics = (home, tab, frame, child, scopes, identityFound) => ({
                documentCount: docs.length,
                homeCount: homes.length,
                tabCount: docs.reduce((count, item) => count + list(item.doc, '[id="task_0000"]').length, 0),
                frameCount: docs.reduce((count, item) => count + list(item.doc, 'iframe[id="win_0000"]').length, 0),
                homeActive: !!home && !!tab && enabled(tab) && tab.classList.contains('taskOn_div'),
                frameVisible: !!frame && visible(frame),
                frameReady: !!frame && frame.getAttribute('isReady') === 'true',
                childFound: !!child,
                childReadyState: child ? ({
                    loading: 'loading',
                    interactive: 'interactive',
                    complete: 'complete'
                }[child.doc.readyState] || 'other') : 'missing',
                accountScopeCount: scopes?.length || 0,
                identityFound: !!identityFound
            });
            if (homes.length !== 1) {
                const diagnostics = homeDiagnostics(null, null, null, null, [], false);
                return result(homes.length > 1 ? 'ambiguous' : 'waiting', 'home', 'home-not-found', diagnostics);
            }
            const home = homes[0], tab = one(home.doc, '[id="task_0000"]'), frame = one(home.doc, 'iframe[id="win_0000"]');
            if (!home.current() || (state.homeDocument && state.homeDocument !== home.doc)) return result('failed', 'home', 'stale');
            const active = enabled(tab) && tab.classList.contains('taskOn_div');
            const child = frame && frame.contentDocument ? docs.find(item => item.doc === frame.contentDocument) : null;
            const scopes = child ? list(child.doc, '.accInfoDiv .accDetailDiv .accInfo').filter(visible) : [];
            const first = scopes[0], id = first && identity(first);
            const diagnostics = homeDiagnostics(home, tab, frame, child, scopes, !!id);
            if (active && visible(frame) && frame.getAttribute('isReady') === 'true') {
                if (child && child.current() && child.doc.readyState === 'complete' && id) {
                    // Reuses the exact homepage account schema already verified for company display.
                    state.homeReady = true; state.homeDocument = home.doc; state.homeScope = first;
                    state.account = id.account; state.company = id.company;
                    return result('home-ready', 'home', undefined, diagnostics);
                }
                return result('waiting', 'home', 'account-identity', diagnostics);
            }
            if (!active && !state.homeClicked) {
                state.homeClicked = true; state.homeDocument = home.doc;
                if (!fresh() || !home.current()) return result('failed', 'home', 'stale');
                home.node.click();
            }
            return result('waiting', 'home', undefined, diagnostics);
        }
        if (!state.homeReady || !originalIdentity()) return result('failed', 'home', 'account-identity');
        if (request.operation === 'open') {
            if (state.opened) return result('opened', 'navigation');
            const item = docs.find(item => item.doc === state.homeDocument);
            if (!item || !item.current()) return result('failed', 'navigation', 'stale');
            if (!state.accountMenuClicked) {
                const menus = list(item.doc, '#menu1Content a').filter(node => enabled(node) && text(node) === '账户查询' && handler(node, 'onclick', 'SJCJ_MENU_1'));
                if (menus.length !== 1) return result(menus.length > 1 ? 'ambiguous' : 'unsupported', 'navigation', 'account-menu');
                state.accountMenuClicked = true; menus[0].click();
                return result('waiting', 'navigation');
            }
            const entries = list(item.doc, 'a').filter(node => enabled(node) && text(node) === '电子回单查询' && handler(node, 'onclick', 'taskBar.createWin'));
            if (entries.length !== 1) return result(entries.length > 1 ? 'ambiguous' : 'waiting', 'navigation', 'receipt-menu');
            if (!fresh() || !item.current()) return result('failed', 'navigation', 'stale');
            state.oldDocuments = docs.map(item => item.doc); state.opened = true;
            entries[0].click();
            return result('opened', 'navigation');
        }
        if (!state.opened) return result('failed', 'navigation', 'not-opened');
        const bankForm = (form, item) => {
            const url = new URL(form.getAttribute('action') || '', item.baseUri);
            return trusted(url) && isBusinessPath(url.pathname) && form.method.toLowerCase() === 'post' &&
                (form.id === 'jhform' || form.name === 'jhform') && handler(form, 'onsubmit', 'go');
        };
        const fields = (form, name) => list(form, '[name]').filter(node => node.name === name && node.form === form);
        const field = (form, name) => { const all = fields(form, name); return all.length === 1 ? all[0] : null; };
        const visibleField = (form, name) => { const all = fields(form, name).filter(visible); return all.length === 1 ? all[0] : null; };
        const candidates = [];
        for (const item of docs) for (const form of list(item.doc, 'form')) {
            if (!visible(form) || !bankForm(form, item)) continue;
            if (one(form, 'select[name="hdType"]') && one(form, 'select[name="ACCT_NO"]')) candidates.push({ ...item, form, stage: 'query' });
            else if (one(form, 'select[name="FLAG"]') && one(form, 'select[name="ACCT_NO"]')) {
                const hasResults = list(form, '.titleTxt').some(node => visible(node) && text(node) === '回单查询结果');
                candidates.push({ ...item, form, combined: true, stage: state.queried && item.doc !== state.queryDocument && hasResults ? 'results' : 'query' });
            }
            else if (one(form, 'input[name="ACCT_NO"][type="hidden"]') && one(form, 'input[name="BEGIN_DATE"][type="hidden"]') &&
                one(form, 'input[name="END_DATE"][type="hidden"]') && list(form, '.titleTxt').some(node => visible(node) && text(node) === '批量回单查询结果')) candidates.push({ ...item, form, stage: 'results' });
        }
        if (candidates.length > 1) return result('ambiguous', 'ambiguous', 'multiple-forms');
        if (!candidates.length) return result('waiting', state.queried ? 'results' : 'query');
        const candidate = candidates[0], { doc, form, stage, combined } = candidate;
        // Wait for account options and result metadata to finish parsing before validating them.
        if (doc.readyState !== 'complete') return result('waiting', stage);
        const canAct = node => fresh() && candidate.current() && bankForm(form, candidate) && enabled(node) && node.form === form;
        if (request.operation === 'query') {
            if (state.queried) return result('queried', 'query');
            if (state.preparing) return result('waiting', 'query');
            if (stage !== 'query' || state.oldDocuments.includes(doc)) return result('waiting', 'query');
            const validMonth = () => {
                if (!/^\d{8}$/.test(request.startDate || '') || !/^\d{8}$/.test(request.endDate || '')) return false;
                const year = Number(request.startDate.slice(0, 4)), month = Number(request.startDate.slice(4, 6));
                return year >= 2000 && year <= 9999 && month >= 1 && month <= 12 && request.startDate.slice(6) === '01' &&
                    request.endDate === request.startDate.slice(0, 6) + String(new Date(year, month, 0).getDate()).padStart(2, '0');
            };
            if (!validMonth()) return result('failed', 'query', 'date-range');
            state.preparing = true;
            try {
                const set = (node, value) => {
                    if (!canAct(node) || node.readOnly) return false;
                    if (node.value === value) return true;
                    const win = node.ownerDocument.defaultView;
                    const prototype = node.tagName === 'SELECT' ? win.HTMLSelectElement.prototype : win.HTMLInputElement.prototype;
                    Object.getOwnPropertyDescriptor(prototype, 'value').set.call(node, value);
                    node.dispatchEvent(new win.Event('input', { bubbles: true }));
                    if (!canAct(node) || node.value !== value) return false;
                    node.dispatchEvent(new win.Event('change', { bubbles: true }));
                    return canAct(node) && node.value === value;
                };
                const choose = (name, label) => {
                    const select = combined ? visibleField(form, name) : field(form, name);
                    if (!select || select.tagName !== 'SELECT' || !canAct(select)) return null;
                    const matches = [...select.options].filter(option => !option.disabled && text(option) === label);
                    return matches.length === 1 ? { select, value: matches[0].value } : null;
                };
                const kind = choose(combined ? 'FLAG' : 'hdType', '批量回单查询');
                if (!kind || !handler(kind.select, 'onchange', 'changeDetailType') || !set(kind.select, kind.value)) return result('failed', 'query', 'query-type');
                const chosen = choose('ACCT_NO', (state.account + state.company).replace(/\s+/g, ''));
                const print = choose('PRINT_FLAG', '全部'), direction = combined ? null : choose('DEAL_DIRECTION', '全部');
                const directions = combined ? fields(form, 'DEAL_DIRECTION').filter(node => node.type === 'checkbox' && visible(node)) : [];
                const start = field(form, 'BEGIN_DATE'), end = field(form, 'END_DATE');
                const submit = one(form, 'input[id="subbutton"][name="Submit1"][type="submit"]');
                const radioFor = (name, fixedLabel) => {
                    const all = fields(form, name).filter(node => node.matches('input[type="radio"]') && visible(node));
                    const matches = all.filter(node => {
                        const labels = [...(node.labels || [])].map(label => text(label));
                        if (labels.includes(fixedLabel)) return true;
                        // Read a small wrapper or direct adjacent text; never infer option order/value.
                        const parent = node.parentElement;
                        if (parent && parent.querySelectorAll('input').length === 1 && text(parent) === fixedLabel) return true;
                        let following = '', sibling = node.nextSibling;
                        for (let n = 0; sibling && n < 4; n++, sibling = sibling.nextSibling) {
                            if (sibling.nodeType !== 3) break;
                            following += sibling.nodeValue || '';
                        }
                        return following.replace(/\s+/g, '') === fixedLabel;
                    });
                    return matches.length === 1 && canAct(matches[0]) ? matches[0] : null;
                };
                const pdf = radioFor('Data_Fmt', 'PDF'), merge = combined ? (radioFor('PER_PAGE', '合并(三联)') || radioFor('PER_PAGE', '合并(单联)')) : radioFor('Mrg_Dwld_Ind', '是');
                if (!chosen || !print || (!combined && !direction) || (combined && (directions.length !== 2 || !directions.some(node => node.value === '1') || !directions.some(node => node.value === '2'))) ||
                    !start || !end || !submit || !canAct(submit) || !pdf || !merge || (combined && !handler(merge, 'onclick', 'changeHBType'))) return result('failed', 'query', 'query-controls');
                const clear = ['MIN_AMT', 'MAX_AMT', 'DOC_NO'].map(name => field(form, name));
                const fuzzy = one(form, combined ? 'input[id="down_ser"]' : 'input[id="ser"]');
                if (clear.some(node => !node) || !fuzzy) return result('failed', 'query', 'query-controls');
                const settings = [[start, request.startDate], [end, request.endDate], [chosen.select, chosen.value], [print.select, print.value], ...(direction ? [[direction.select, direction.value]] : []), ...clear.map(node => [node, '']), [fuzzy, '']];
                for (const [node, value] of settings) if (!set(node, value)) return result('failed', 'query', 'field-readback');
                for (const checkbox of directions) {
                    if (!canAct(checkbox)) return result('failed', 'query', 'field-readback');
                    if (!checkbox.checked) checkbox.click();
                    if (!canAct(checkbox) || !checkbox.checked) return result('failed', 'query', 'field-readback');
                }
                for (const radio of [pdf, merge]) {
                    // The combined template leaves Mrg_Dwld_Ind empty until its original
                    // merge handler runs, even when the default radio is already checked.
                    if (!radio.checked || (combined && radio === merge)) radio.click();
                    if (!canAct(radio) || !radio.checked) return result('failed', 'query', 'field-readback');
                }
                const mergeValue = combined ? field(form, 'Mrg_Dwld_Ind')?.value : merge.value;
                if (combined && mergeValue !== '1') return result('failed', 'query', 'field-readback');
                if (!fresh() || !originalIdentity() || !candidate.current() || !canAct(submit) || !handler(form, 'onsubmit', 'go') ||
                    settings.some(([node, value]) => !canAct(node) || node.value !== value) || directions.some(node => !canAct(node) || !node.checked) || !pdf.checked || !merge.checked) return result('failed', 'query', 'stale');
                state.optionValue = chosen.value; state.startDate = request.startDate; state.endDate = request.endDate;
                state.queryDocument = doc; state.queryChain = candidate.chain.map(pair => pair.frame);
                state.combined = !!combined;
                state.expectedFilters = { Data_Fmt: pdf.value, Mrg_Dwld_Ind: mergeValue, PRINT_FLAG: print.value, DEAL_DIRECTION: combined ? ['1', '2'] : direction.value,
                    MIN_AMT: '', MAX_AMT: '', DOC_NO: '' };
                if (combined) { state.expectedFilters.FLAG = kind.value; state.expectedFilters.PER_PAGE = merge.value; }
                state.queried = true; // Consume before the bank's submit handlers can reenter.
                // go() may legitimately prevent default and submit through the bank's own code.
                // Only a new matching result proves success; the host bounds this wait.
                submit.click();
                return result('queried', 'query');
            } finally { state.preparing = false; }
        }
        if (!state.queried) return result('failed', stage, 'not-queried');
        if (stage !== 'results' || doc === state.queryDocument) return result('waiting', 'results');
        const sameLineage = candidate.chain.length === state.queryChain.length && candidate.chain.every((pair, i) => pair.frame === state.queryChain[i]);
        const matches = (name, value) => {
            if (!combined) { const node = field(form, name); return node && node.type === 'hidden' && node.value === value; }
            if (name === 'DEAL_DIRECTION') {
                const checked = fields(form, name).filter(node => visible(node) && node.type === 'checkbox' && node.checked);
                return checked.length === value.length && value.every(expected => checked.filter(node => node.value === expected).length === 1);
            }
            if (name === 'Data_Fmt' || name === 'PER_PAGE') {
                const checked = fields(form, name).filter(node => visible(node) && node.type === 'radio' && node.checked);
                return checked.length === 1 && checked[0].value === value;
            }
            const node = name === 'Mrg_Dwld_Ind' ? field(form, name) : visibleField(form, name);
            return node && node.value === value;
        };
        const resultIdentity = () => fresh() && candidate.current() && bankForm(form, candidate) && originalIdentity() && sameLineage &&
            !!combined === state.combined &&
            matches('ACCT_NO', state.optionValue) && matches('BEGIN_DATE', state.startDate) && matches('END_DATE', state.endDate) &&
            Object.entries(state.expectedFilters).every(([name, value]) => matches(name, value));
        if (!resultIdentity()) return result('failed', 'results', 'result-identity');
        const rows = fields(form, 'KEYVALUE').filter(node => node.matches('input[type="checkbox"]'));
        if (!rows.length) return result('empty', 'results');
        const firstPage = () => {
            const pager = one(form, '.splitPage');
            if (!visible(pager)) return false;
            const previous = list(pager, 'a,span,button').filter(node => text(node).replace(/[<>＜＞〈〉《》‹›]/g, '') === '上一页');
            return previous.length === 1 && previous[0].tagName === 'SPAN' && visible(previous[0]) &&
                !previous[0].hasAttribute('onclick') && !previous[0].hasAttribute('href') && !previous[0].closest('a,button');
        };
        if (!firstPage()) return result('failed', 'results', 'not-first-page');
        if (request.operation === 'identify') {
            if (state.resultDocument && state.resultDocument !== doc) return result('failed', 'results', 'stale');
            state.resultDocument = doc;
            state.naming = { companyName: state.company, accountLast4: state.account.slice(-4) };
            return { status: 'identified', stage: 'results', ...state.naming };
        }
        if (!state.naming || state.resultDocument !== doc) return result('failed', 'results', 'not-identified');
        if (request.operation === 'verify-download') return result(state.downloadConsumed && resultIdentity() && firstPage() ? 'verified' : 'failed', 'results');
        if (request.operation === 'download') {
            if (state.downloadConsumed) return result('downloading', 'results');
            if (state.selecting) return result('waiting', 'results');
            const all = field(form, 'all'), print = one(form, 'input[id="Submit8"][name="Submit8"][type="button"]');
            if (!all || all.type !== 'checkbox' || !handler(all, 'onclick', 'selectall') || !canAct(all) ||
                !print || !handler(print, 'onclick', 'HDPrint') || !canAct(print) || rows.some(node => !canAct(node) || !handler(node, 'onclick', 'trClick'))) return result('failed', 'results', 'print-controls');
            state.selecting = true;
            try {
                if (!rows.every(node => node.checked)) {
                    if (state.selectionConsumed) return result('failed', 'results', 'selection-failed');
                    state.selectionConsumed = true;
                    // Never toggle a selected all-checkbox off. Inconsistent bank state is a failure.
                    if (all.checked) return result('failed', 'results', 'selection-failed');
                    all.click();
                }
                const currentRows = fields(form, 'KEYVALUE');
                if (!resultIdentity() || !firstPage() || !canAct(print) || !handler(print, 'onclick', 'HDPrint') || currentRows.length !== rows.length ||
                    currentRows.some((node, i) => node !== rows[i] || !canAct(node) || !node.checked)) return result('failed', 'results', 'selection-failed');
                state.downloadConsumed = true;
                print.click();
                return result('downloading', 'results');
            } finally { state.selecting = false; }
        }
        return result('unsupported', stage);
    } catch (_) {
        return result('failed', 'unsupported', 'page-changed');
    }
}
