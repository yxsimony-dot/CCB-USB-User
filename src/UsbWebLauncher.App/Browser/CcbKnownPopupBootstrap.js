(install) => {
    'use strict';
    const key = '__usbWebLauncherKnownPopupBootstrapV1';
    const topAllowed = () => {
        try {
            const url = new URL(window.document.URL);
            return window === window.top && url.protocol === 'https:' &&
                (!url.port || url.port === '443') && !url.username && !url.password &&
                (url.hostname === 'b2b3.ccb.com' || url.hostname === 'xc.b2b3.ccb.com') &&
                url.pathname === '/NCCB/V6/b2bindex.jsp';
        } catch (_) { return false; }
    };
    if (!topAllowed() || typeof install !== 'function') return 'unsupported';
    const rootDocument = document;
    const origin = new URL(rootDocument.URL).origin;
    if (rootDocument[key]) {
        rootDocument[key].scan();
        return 'already-installed';
    }
    let active = true;
    let scheduled = false;
    const allowed = doc => {
        try {
            const url = new URL(doc.URL);
            return url.origin === origin && !url.username && !url.password &&
                url.pathname.startsWith('/NCCB/') && doc.defaultView.document === doc;
        } catch (_) { return false; }
    };
    const schedule = () => {
        if (!active || scheduled) return;
        scheduled = true;
        queueMicrotask(() => { scheduled = false; scan(); });
    };
    const observe = doc => {
        if (doc[key]) return doc[key];
        const realm = doc.defaultView;
        const listeners = new Set();
        let observing = false;
        let suspended = false;
        const observer = new realm.MutationObserver(schedule);
        const stop = () => {
            observing = false;
            suspended = true;
            observer.disconnect();
            for (const frame of listeners) frame.removeEventListener('load', schedule);
            listeners.clear();
        };
        const bind = frames => {
            if (suspended) return;
            if (!observing) {
                observer.observe(doc, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
                observing = true;
            }
            // Retain only this document's current elements, never old child documents.
            const current = new Set(frames);
            for (const frame of listeners) {
                if (!current.has(frame)) { frame.removeEventListener('load', schedule); listeners.delete(frame); }
            }
            for (const frame of frames) {
                if (!listeners.has(frame)) { listeners.add(frame); frame.addEventListener('load', schedule); }
            }
        };
        const resume = () => { suspended = false; };
        realm.addEventListener('pagehide', stop);
        realm.addEventListener('pageshow', event => {
            if (event.persisted) { resume(); schedule(); }
        });
        const state = { bind, stop, resume, scan: () => scan(), get suspended() { return suspended; } };
        doc[key] = state;
        return state;
    };
    const walk = (visit) => {
        const visited = new Set();
        const descend = (doc, depth) => {
            if (depth > 4 || visited.size >= 16 || !doc || visited.has(doc) || !allowed(doc)) return;
            visited.add(doc);
            let frames;
            try { frames = Array.from(doc.querySelectorAll('frame,iframe')); } catch (_) { return; }
            if (visit(doc, frames) === false) return;
            if (depth === 4) return;
            for (const frame of frames) {
                if (visited.size >= 16) break;
                // The browser's current document is authoritative; declared src may be stale.
                try { descend(frame.contentDocument, depth + 1); } catch (_) { }
            }
        };
        descend(rootDocument, 0);
    };
    const scan = () => {
        if (!active || !topAllowed() || document !== rootDocument) return;
        walk((doc, frames) => {
            const state = observe(doc);
            if (state.suspended) return false;
            state.bind(frames);
            try { install(doc.defaultView); } catch (_) { }
        });
    };
    window.addEventListener('pagehide', () => {
        active = false;
        walk(doc => { if (doc[key]) doc[key].stop(); });
    });
    window.addEventListener('pageshow', event => {
        if (!event.persisted || !topAllowed()) return;
        active = true;
        walk(doc => { if (doc[key]) doc[key].resume(); });
        schedule();
    });
    scan();
    return 'installed';
}
