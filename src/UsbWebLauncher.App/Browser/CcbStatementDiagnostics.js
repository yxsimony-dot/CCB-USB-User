() => {
    // One bounded, read-only snapshot. Never return input values or arbitrary page text.
    const result = { status: 'unsupported', documents: [] };
    const limits = { documents: 16, depth: 4, nodes: 6000, controls: 60, fields: 80, forms: 12, frames: 32, scripts: 20 };
    const labels = [
        ['account-query', '账户查询'], ['account-info', '账户信息查询'],
        ['current-account', '活期账户', '活期查询'], ['transaction-detail', '交易明细'],
        ['confirm', '确定'], ['download-all', '下载全部'], ['pdf-download', 'PDF下载'],
        ['home', '首页'], ['receipt-query', '电子回单查询'], ['receipt-batch-query', '批量回单查询'],
        ['receipt-results', '批量回单查询结果'], ['query-type', '查询类型'], ['date-range', '起止日期'],
        ['print-status', '打印状态'], ['file-format', '文件格式'], ['merge-download', '合并下载'],
        ['select-all', '全选'], ['receipt-preview-print', '电子回单预览/打印'],
        ['previous-page', '上一页'], ['next-page', '下一页'], ['page-jump', '到第', '转至']
    ];
    const seen = new Set();
    const references = new WeakMap();
    let nextReference = 1;
    let root;
    function decode(value) {
        let text = String(value || '').slice(0, 1024);
        for (let i = 0; i < 3; i++) {
            try { const next = decodeURIComponent(text); if (next === text) break; text = next; }
            catch (_) { break; }
        }
        return text;
    }
    function redact(value) {
        return decode(value)
            .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, 'REDACTED')
            .replace(/[0-9a-f]{16,}/gi, 'REDACTED')
            .replace(/\d{6,}/g, 'REDACTED')
            .replace(/[A-Za-z0-9_-]{32,}/g, 'REDACTED');
    }
    function identifier(value) {
        const text = redact(value);
        return /^[A-Za-z_][A-Za-z0-9_.:-]{0,95}$/.test(text) ? text : '';
    }
    function classes(value) {
        return String(value || '').slice(0, 512).split(/\s+/).slice(0, 6).map(identifier).filter(Boolean).join(' ').slice(0, 160);
    }
    function bank(url) {
        return url.protocol === 'https:' && (!url.port || url.port === '443') &&
            (url.hostname === 'ccb.com' || url.hostname.endsWith('.ccb.com')) &&
            (url.pathname === '/NCCB' || url.pathname.startsWith('/NCCB/')) && !url.username && !url.password;
    }
    function parse(value, base) { try { return new URL(value, base); } catch (_) { return null; } }
    function address(value, base) {
        const url = parse(value, base);
        if (!url || !bank(url) || url.origin !== root.origin) return null;
        const path = redact(url.pathname).split('/').slice(0, 16).map(segment =>
            /^[A-Za-z0-9_.~-]{0,80}$/.test(segment) ? segment : 'REDACTED').join('/').slice(0, 240);
        const queryNames = [];
        let count = 0;
        for (const key of url.searchParams.keys()) {
            if (++count > 32) break;
            const name = identifier(key);
            if (name && !queryNames.includes(name)) queryNames.push(name);
        }
        return { origin: root.origin, path, queryNames };
    }
    function attr(node, name) { try { return node.getAttribute(name) || ''; } catch (_) { return ''; } }
    function functionName(source) {
        const match = /^\s*(?:return\s+)?([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*){0,4})\s*\(/.exec(String(source || '').slice(0, 512));
        const name = match ? redact(match[1]) : '';
        return /^[A-Za-z_$][A-Za-z0-9_.$]{0,95}$/.test(name) ? name : '';
    }
    function reference(node) {
        if (!references.has(node)) references.set(node, nextReference++);
        return references.get(node);
    }
    function metadata(node) {
        const handlers = {};
        for (const event of ['click', 'change', 'submit']) {
            const name = functionName(attr(node, 'on' + event));
            if (name) handlers[event] = name;
        }
        const href = attr(node, 'href');
        if (/^javascript:/i.test(href)) {
            const name = functionName(href.slice(11));
            if (name) handlers.href = name;
        }
        let form = '';
        try { if (node.form) form = identifier(attr(node.form, 'id') || attr(node.form, 'name')); } catch (_) { }
        const ancestorRefs = [];
        for (let parent = node.parentElement, level = 0; parent && level < 3; parent = parent.parentElement, level++) ancestorRefs.push(reference(parent));
        const entry = { tag: node.tagName.toLowerCase(), id: identifier(attr(node, 'id')), name: identifier(attr(node, 'name')),
            type: identifier(attr(node, 'type')).toLowerCase(), class: classes(attr(node, 'class')), form, handlers,
            ref: reference(node), ancestorRefs };
        if (entry.tag === 'label') entry.labelFor = identifier(attr(node, 'for'));
        if (entry.tag === 'input' && ['checkbox', 'radio'].includes(entry.type)) entry.checked = node.checked === true;
        return entry;
    }
    function markers(node) {
        // Read direct text nodes only: parent containers cannot copy whole tables into memory/output.
        let text = '', child = node.firstChild, count = 0;
        while (child && count++ < 16 && text.length < 256) {
            if (child.nodeType === 3) text += String(child.nodeValue || '').slice(0, 256 - text.length);
            child = child.nextSibling;
        }
        text = text.replace(/\s+/g, '');
        return labels.filter(label => label.slice(1).some(word => text.includes(word))).map(label => label[0]);
    }
    function inherited(value) { return !value || /^about:(blank|srcdoc)(?:[?#].*)?$/i.test(value) || /^javascript:/i.test(value); }
    function append(list, entry, snapshot) {
        list.push(entry);
        return true;
    }
    function controlMetadata(node, hits) {
        const entry = { ...metadata(node), markers: hits };
        if (hits.length) {
            entry.ancestors = [];
            let parent = node.parentElement;
            for (let level = 0; parent && level < 3; level++, parent = parent.parentElement) {
                if (['a', 'button'].includes(parent.tagName.toLowerCase()) || attr(parent, 'onclick')) entry.ancestors.push(metadata(parent));
            }
        }
        return entry;
    }
    function inspect(doc, depth, parentIndex, declaredInherited, parentBase) {
        if (!doc || seen.has(doc) || depth > limits.depth || result.documents.length >= limits.documents) return;
        let actual;
        try { actual = parse(doc.URL, root.href); } catch (_) { return; }
        const isBlank = actual && /^about:(blank|srcdoc)(?:[?#].*)?$/i.test(actual.href);
        if (!actual || !(bank(actual) && actual.origin === root.origin) && !(depth > 0 && declaredInherited && isBlank)) return;
        let base = isBlank ? parentBase : actual.href;
        try { const candidate = parse(doc.baseURI); if (candidate && bank(candidate) && candidate.origin === root.origin) base = candidate.href; } catch (_) { }
        seen.add(doc);
        const index = result.documents.length;
        const snapshot = { index, parent: parentIndex, depth, address: isBlank ? { origin: root.origin, path: 'about:blank', queryNames: [] } : address(actual.href, root.href),
            markers: [], controls: [], forms: [], fields: [], scripts: [], frames: [],
            counts: { checkbox: 0, radio: 0, select: 0 }, inspectedNodes: 0, truncated: false,
            controlsTruncated: false, fieldsTruncated: false };
        result.documents.push(snapshot);
        const children = [];
        try {
            const walker = doc.createTreeWalker(doc.documentElement, 1);
            let node = walker.currentNode;
            while (node && snapshot.inspectedNodes < limits.nodes) {
                snapshot.inspectedNodes++;
                const tag = node.tagName.toLowerCase();
                const hits = ['script', 'style', 'textarea'].includes(tag) ? [] : markers(node);
                for (const hit of hits) if (!snapshot.markers.includes(hit)) snapshot.markers.push(hit);
                const control = ['a', 'button', 'input', 'select'].includes(tag) || hits.length > 0;
                if (control) {
                    if (snapshot.controls.length < limits.controls) append(snapshot.controls, controlMetadata(node, hits), snapshot);
                    else {
                        snapshot.controlsTruncated = snapshot.truncated = true;
                        if (hits.length) {
                            const replace = snapshot.controls.findIndex(item => item.markers.length === 0);
                            if (replace >= 0) {
                                const entry = controlMetadata(node, hits);
                                snapshot.controls[replace] = entry;
                            }
                        }
                    }
                }
                if (tag === 'form' && snapshot.forms.length < limits.forms) {
                    const method = attr(node, 'method').toLowerCase();
                    append(snapshot.forms, { ...metadata(node), method: method === 'post' ? 'post' : 'get', action: address(attr(node, 'action') || base, base) }, snapshot);
                }
                if (['input', 'select', 'textarea'].includes(tag)) {
                    const type = attr(node, 'type').toLowerCase();
                    if (type === 'checkbox' || type === 'radio') snapshot.counts[type]++;
                    if (tag === 'select') snapshot.counts.select++;
                    if (snapshot.fields.length < limits.fields) {
                        const field = { ...metadata(node), disabled: node.hasAttribute('disabled'), required: node.hasAttribute('required') };
                        if (tag === 'select') field.optionCount = Math.min(node.options.length, 1000);
                        append(snapshot.fields, field, snapshot);
                    } else snapshot.fieldsTruncated = snapshot.truncated = true;
                }
                if (tag === 'script' && snapshot.scripts.length < limits.scripts) {
                    const src = attr(node, 'src'), safe = src ? address(src, base) : null;
                    if (safe && !snapshot.scripts.some(item => item.path === safe.path)) append(snapshot.scripts, safe, snapshot);
                }
                node = walker.nextNode();
            }
            snapshot.truncated = snapshot.truncated || !!node;
            // Stable order makes equivalent structural snapshots deduplicate despite DOM label order.
            snapshot.markers.sort((a, b) => labels.findIndex(l => l[0] === a) - labels.findIndex(l => l[0] === b));
        } catch (_) { snapshot.truncated = true; }
        // Discover bounded frame collections independently: large menus must not hide the business frame.
        let frameCount = 0;
        for (const tag of ['iframe', 'frame']) {
            try {
                const frames = doc.getElementsByTagName(tag);
                for (let i = 0; i < frames.length && frameCount < limits.frames; i++, frameCount++) {
                    const node = frames[i], src = attr(node, 'src'), legacy = inherited(src), safe = legacy ? null : address(src, base);
                    append(snapshot.frames, { ...metadata(node), address: safe, inherited: legacy }, snapshot);
                    if (legacy || safe) children.push({ node, legacy });
                }
            } catch (_) { snapshot.truncated = true; }
        }
        if (depth < limits.depth) {
            for (const child of children) {
                if (result.documents.length >= limits.documents) break;
                try { inspect(child.node.contentDocument, depth + 1, index, child.legacy, base); } catch (_) { }
            }
        }
    }
    try {
        root = parse(window.location.href);
        if (!root || !bank(root)) return result;
        result.status = 'diagnostic';
        inspect(document, 0, null, false, root.href);
        // Trim collected metadata fairly across documents; retain fixed markers and prefer workflow targets.
        // All output is ASCII after sanitization, so character and UTF-8 byte budgets agree.
        let length = JSON.stringify(result).length;
        const maxLength = 96 * 1024;
        function trim(arrayName, eligible) {
            let removed;
            do {
                removed = false;
                for (const snapshot of result.documents) {
                    if (length <= maxLength) return;
                    const list = snapshot[arrayName];
                    for (let i = list.length - 1; i >= 0; i--) {
                        if (!eligible(list[i], i, list)) continue;
                        length -= JSON.stringify(list[i]).length; // Conservative: excludes comma/false-to-true savings.
                        list.splice(i, 1);
                        snapshot.truncated = true;
                        if (arrayName === 'controls') snapshot.controlsTruncated = true;
                        if (arrayName === 'fields') snapshot.fieldsTruncated = true;
                        removed = true;
                        break;
                    }
                }
            } while (removed && length > maxLength);
        }
        trim('controls', entry => entry.markers.length === 0);
        trim('scripts', () => true);
        trim('fields', () => true);
        trim('frames', () => true);
        trim('controls', (entry, index, list) => list.slice(0, index).some(other => JSON.stringify(other.markers) === JSON.stringify(entry.markers)));
        trim('forms', (_, __, list) => list.length > 1);
        trim('controls', (_, __, list) => list.length > 1);
        trim('forms', () => true);
        trim('controls', () => true);
    } catch (_) { }
    return result;
}
