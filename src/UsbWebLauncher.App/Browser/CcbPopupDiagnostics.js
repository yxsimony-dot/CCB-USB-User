() => {
    // Read-only, bounded snapshot. No waiting, observers, events, clicks or DOM writes.
    const documents = [];
    const clean = value => String(value || '').replace(/\d{6,}/g, '[digits]').slice(0, 160);
    const parse = (raw, base) => { try { return new URL(raw, base); } catch { return null; } };
    const allowed = url => url && url.protocol === 'https:' && (!url.port || url.port === '443') &&
        (url.hostname === 'ccb.com' || url.hostname.endsWith('.ccb.com')) && url.pathname.startsWith('/NCCB/');
    const rootUrl = parse(window.document.URL);
    if (!allowed(rootUrl)) return { status: 'unsupported', documents };
    const address = url => url && /^(https?:)$/.test(url.protocol)
        ? { origin: clean(url.origin), path: clean(url.pathname) } : null;
    const inherited = url => url && url.protocol === 'about:' && /^(blank|srcdoc)$/.test(url.pathname);
    const attrs = element => ({
        tag: element.tagName.toLowerCase(), id: clean(element.getAttribute('id')),
        class: clean(element.getAttribute('class')), role: clean(element.getAttribute('role')),
        type: clean(element.getAttribute('type'))
    });
    const visible = element => {
        if (!element.isConnected || element.getClientRects().length === 0) return false;
        for (let parent = element; parent; parent = parent.parentElement) {
            const style = parent.ownerDocument.defaultView.getComputedStyle(parent);
            if (parent.hidden || style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
        }
        return true;
    };
    const containerName = element => /dialog|layer|modal|popup/i.test(
        `${element.getAttribute('id') || ''} ${element.getAttribute('class') || ''} ${element.getAttribute('role') || ''}`);
    const onclickFunction = element => {
        const match = (element.getAttribute('onclick') || '').match(/^\s*(?:return\s+)?([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*\(/);
        return match ? clean(match[1]) : '';
    };
    const closeName = element => /close|colse|关闭|×/i.test(
        `${element.getAttribute('id') || ''} ${element.getAttribute('class') || ''} ${element.getAttribute('aria-label') || ''} ${element.getAttribute('title') || ''} ${onclickFunction(element)}`);
    const mobilePanel = element => element.id === 'top_sjyl_type1' || element.classList.contains('mob_drain');
    const leafText = element => {
        if (element.childElementCount || /^(INPUT|TEXTAREA|SELECT|OPTION|SCRIPT|STYLE|NOSCRIPT)$/.test(element.tagName)) return '';
        // Only short leaf labels are inspected for known public titles; none are emitted.
        const text = element.textContent || '';
        return text.length <= 120 ? text.replace(/\s+/g, '') : '';
    };
    const titles = [['企业手机银行', 'mobile-banking'], ['温馨提示', 'warm-notice'],
        ['受益所有人信息备案的通知', 'beneficial-owner-notice']];
    const visited = new Set();
    const visit = (document, depth, parentVisible, parentUrl = null) => {
        if (depth > 4 || documents.length >= 16) return 'limit';
        if (visited.has(document)) return 'visited';
        const url = parse(document.URL);
        const isInherited = !!parentUrl && inherited(url);
        if (!isInherited && (!allowed(url) || url.origin !== rootUrl.origin)) return 'blocked-document';
        const baseUrl = isInherited ? parentUrl : url;
        visited.add(document);
        const index = documents.length;
        const record = { ...(isInherited ? { origin: clean(rootUrl.origin), path: `about:${url.pathname}` } : address(url)),
            readyState: document.readyState, frames: [], candidates: [], panels: [] };
        documents.push(record);
        const candidates = new Map();
        const panels = new Map();
        const panelNodes = new Set();
        const addPanel = element => {
            if (panels.has(element) || panels.size >= 3) return;
            const panel = { ...attrs(element), visible: parentVisible && visible(element), markers: ['mobile-banking'], nodes: [] };
            panels.set(element, panel);
            for (const node of Array.from(element.querySelectorAll('*')).slice(0, 300)) {
                if (panelNodes.size >= 60) break;
                if (panelNodes.has(node) || /^(INPUT|TEXTAREA|SELECT|OPTION|SCRIPT|STYLE|NOSCRIPT)$/.test(node.tagName)) continue;
                panelNodes.add(node);
                const structure = { ...attrs(node), onclickFunction: onclickFunction(node),
                    visible: parentVisible && visible(node), closeCandidate: closeName(node) || /^(×|关闭|关 闭)$/.test(leafText(node)) };
                if (node.tagName === 'IMG') structure.image = address(parse(node.getAttribute('src') || '', baseUrl.href));
                panel.nodes.push(structure);
            }
        };
        const add = (element, marker, closeCandidate) => {
            if (!candidates.has(element)) {
                if (candidates.size >= 30) return;
                const ancestors = [];
                for (let parent = element.parentElement; parent && ancestors.length < 4; parent = parent.parentElement) ancestors.push(attrs(parent));
                candidates.set(element, { ...attrs(element), document: index,
                    visible: parentVisible && visible(element), markers: [], closeCandidate: false,
                    onclickFunction: onclickFunction(element), ancestors });
            }
            const candidate = candidates.get(element);
            if (marker && !candidate.markers.includes(marker)) candidate.markers.push(marker);
            candidate.closeCandidate ||= closeCandidate;
        };
        // Prioritize recognized notice titles before generic close/container names.
        const elements = Array.from(document.querySelectorAll('body *')).slice(0, 6000);
        for (const element of elements) {
            if (mobilePanel(element)) { add(element, 'mobile-banking', false); addPanel(element); }
        }
        for (const element of elements) {
            const text = leafText(element);
            const marker = titles.find(([title]) => text.includes(title))?.[1];
            if (!marker) continue;
            add(element, marker, false);
            let container = element.parentElement;
            for (let parent = element.parentElement, count = 0; parent && parent !== document.body && count < (marker === 'mobile-banking' ? 6 : 4); parent = parent.parentElement, count++) {
                if (marker === 'mobile-banking') container = parent;
                if (containerName(parent) || mobilePanel(parent)) { container = parent; break; }
            }
            if (!container || container === document.body) continue;
            add(container, marker, false);
            if (marker === 'mobile-banking') addPanel(container);
            for (const child of Array.from(container.querySelectorAll('*')).slice(0, 300)) {
                if (closeName(child) || /^(×|关闭|关 闭)$/.test(leafText(child))) add(child, marker, true);
            }
        }
        for (const element of elements) {
            if (closeName(element)) add(element, null, true);
            else if (containerName(element)) add(element, null, false);
        }
        record.candidates = Array.from(candidates.values());
        record.panels = Array.from(panels.values());
        for (const frame of Array.from(document.querySelectorAll('iframe,frame')).slice(0, 32)) {
            const source = frame.getAttribute('src') || '';
            const frameUrl = parse(source, baseUrl.href);
            const srcKind = frame.hasAttribute('srcdoc') ? 'srcdoc' : !source.trim() || (inherited(frameUrl) && frameUrl.pathname === 'blank') ? 'blank' :
                inherited(frameUrl) ? 'srcdoc' : frameUrl?.protocol === 'javascript:' ? 'javascript' :
                    frameUrl?.protocol === 'https:' ? 'https' : frameUrl?.protocol === 'http:' ? 'http' : 'other';
            const frameVisible = parentVisible && visible(frame);
            const frameRecord = { ...attrs(frame), address: /^(http|https)$/.test(srcKind) ? address(frameUrl) : null,
                srcKind, inspection: 'blocked-declared', visible: frameVisible };
            record.frames.push(frameRecord);
            // Legacy blank/javascript declarations are never evaluated. Access the current
            // DOM only; browser same-origin enforcement plus the actual URL gate apply below.
            const inheritedDeclaration = /^(blank|srcdoc|javascript)$/.test(srcKind);
            if (!inheritedDeclaration && (!allowed(frameUrl) || frameUrl.origin !== rootUrl.origin)) continue;
            if (depth >= 4 || documents.length >= 16) { frameRecord.inspection = 'limit'; continue; }
            frameRecord.inspection = 'unavailable';
            try {
                const child = frame.contentDocument;
                if (child?.defaultView) frameRecord.inspection = visit(child, depth + 1, frameVisible, baseUrl);
            } catch { /* Cross-origin or navigating frames are outside this snapshot. */ }
        }
        return 'inspected';
    };
    visit(window.document, 0, true);
    return { status: 'diagnostic', documents };
}
