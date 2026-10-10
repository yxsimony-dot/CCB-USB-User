(request) => {
    'use strict';
    // Read-only identity probe. No exporter state, account data, or page text is returned.
    const reply = status => ({ status });
    try {
        if (!request || typeof request.origin !== 'string' || window !== window.top) return reply('unsupported');
        const trusted = url => url.protocol === 'https:' && !url.username && !url.password && !url.port &&
            ['b2b3.ccb.com', 'xc.b2b3.ccb.com'].includes(url.hostname) && url.origin === request.origin;
        const permitted = url => trusted(url) && url.pathname.startsWith('/NCCB/');
        const root = window.document;
        if (!permitted(new URL(window.location.href))) return reply('unsupported');
        const list = (node, selector) => Array.from(node.querySelectorAll(selector));
        const one = (node, selector) => { const nodes = list(node, selector); return nodes.length === 1 ? nodes[0] : null; };
        const visible = node => {
            if (!node || !node.isConnected || !node.getClientRects().length) return false;
            for (let current = node; current; current = current.parentElement) {
                const css = current.ownerDocument.defaultView.getComputedStyle(current);
                if (current.hidden || css.display === 'none' || css.visibility === 'hidden' ||
                    css.visibility === 'collapse' || css.opacity === '0') return false;
            }
            return true;
        };
        const companyName = raw => {
            if (typeof raw !== 'string' || /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff]|…|\.{3}/u.test(raw)) return null;
            return raw.trim() || null;
        };
        const accountNumber = raw => /^\d+$/.test((raw || '').trim()) ? raw.trim() : null;
        const docs = [];
        const seen = new Set();
        let exhausted = false;
        const walk = (doc, depth, inherited, baseUri) => {
            if (seen.has(doc)) return;
            const url = new URL(doc.location.href);
            if (!(inherited && url.href === 'about:blank') && !permitted(url)) return;
            if (depth > 4 || docs.length >= 16) { exhausted = true; return; }
            seen.add(doc); docs.push(doc);
            const base = doc.baseURI && doc.baseURI !== 'about:blank' ? doc.baseURI : baseUri;
            for (const frame of list(doc, 'iframe,frame')) {
                if (!visible(frame)) continue;
                try {
                    const src = (frame.getAttribute('src') || '').trim();
                    const blank = !src || src === 'about:blank' || /^javascript:/i.test(src);
                    if (!blank && !permitted(new URL(src, base))) continue;
                    const child = frame.contentDocument;
                    if (child && child.defaultView) walk(child, depth + 1, blank, base);
                } catch (_) { /* Cross-origin and navigating documents are not readable candidates. */ }
                if (exhausted) return;
            }
        };
        walk(root, 0, false, window.location.href);
        // A visible login control invalidates stale identity in any remaining business frame.
        if (docs.some(doc => list(doc, 'input[type="password"],#LOG_PWD,#LOGIN_BOX_BTN').some(visible))) return reply('waiting');
        if (exhausted) return reply('ambiguous');
        const names = new Set();
        for (const doc of docs) {
            // These exact homepage fields were observed in the bank's cached template.
            for (const scope of list(doc, '.accInfoDiv .accDetailDiv .accInfo')) {
                const name = one(scope, '#ACCNM_ZHGS.deptName');
                const account = one(scope, '#ACCNO_ZHGS.accNo');
                if (!visible(name) || !visible(account) || !accountNumber(account.textContent)) continue;
                const value = companyName(name.textContent);
                if (value) names.add(value);
            }
            // Result-page fallback requires the verified visible four-cell identity row
            // and agreement with both hidden identity fields in the same bank form.
            for (const form of list(doc, 'form[name="jhform"]')) {
                const table = one(form, 'table#headTable.tableConfirm');
                const hiddenName = one(form, 'input[name="checkedAccName"][type="hidden"]');
                const hiddenAccount = one(form, 'input[name="checkedAccno"][type="hidden"]');
                if (!visible(table) || !hiddenName || !hiddenAccount) continue;
                const candidates = [];
                for (const row of list(table, 'tr')) {
                    const cells = Array.from(row.children);
                    if (cells.length !== 4 || !cells.every(cell => cell.tagName === 'TD' && visible(cell))) continue;
                    if (!cells[0].matches('.cfmTitle.cfmSty1') || !cells[1].matches('.cfmInfo.cfmSty4.acc') ||
                        !cells[2].matches('.cfmTitle.cfmSty1') || !cells[3].matches('.cfmInfo.cfmSty4.fzWord')) continue;
                    if (cells[0].textContent.replace(/\s/g, '') !== '账号：' || cells[2].textContent.replace(/\s/g, '') !== '账户名称：') continue;
                    const name = companyName(cells[3].textContent);
                    const account = accountNumber(cells[1].textContent);
                    if (name && account && name === companyName(hiddenName.value) && account === accountNumber(hiddenAccount.value)) candidates.push(name);
                }
                if (candidates.length === 1) names.add(candidates[0]);
            }
        }
        if (names.size > 1) return reply('ambiguous');
        if (names.size === 1) return { status: 'ready', companyName: names.values().next().value };
        return reply('waiting');
    } catch (_) {
        // DOM navigation races should clear the display and allow a later probe.
        return reply('waiting');
    }
}
