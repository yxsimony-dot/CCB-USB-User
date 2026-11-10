function (operation) {
    'use strict';
    const slot = '__usbLauncherPsbcInputTrace';
    function allowed() {
        const u = new URL(location.href);
        return window === window.top && u.protocol === 'https:' &&
            u.hostname === 'corpebank.psbc.com' && (!u.port || u.port === '443') &&
            !u.username && !u.password && u.pathname === '/' && !u.search &&
            (u.hash === '#/login' || u.hash === '#/login?tab=ukeyLogin');
    }
    if (operation === 'stop' || !allowed()) {
        if (window[slot]) window[slot].stop();
        return [];
    }
    if (operation !== 'drain') return [];
    if (!window[slot]) {
        const started = performance.now();
        const types = ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click',
            'focus', 'blur', 'keydown', 'keyup', 'keypress', 'beforeinput', 'input',
            'compositionstart', 'compositionupdate', 'compositionend', 'paste'];
        let queue = [], closed = false, total = 0, previous = '';
        const bit = value => value === 0 || value === 1 ? value : -1;
        function field() {
            return document.getElementById('ukeyLogin_div');
        }
        function sample(eventType, trusted) {
            const target = field();
            const mask = document.getElementById('ukeyLogin_input');
            if (!target || !mask || !target.contains(mask)) return null;
            // Read only the rendered mask, never input values or keyboard content.
            const text = mask.textContent || '';
            const maskLength = text.length <= 64 && /^[\u25cf*]*$/.test(text) ? text.length : -1;
            const busy = target.getAttribute('isbusy');
            return { eventType, elapsedMs: Math.min(600000, Math.floor(performance.now() - started)),
                maskLength, documentFocused: document.hasFocus(),
                fieldFocused: target === document.activeElement || target.contains(document.activeElement),
                trusted, connected: bit(window.g_input_connectFlag),
                stopped: bit(window.g_stopSendRequest),
                busy: busy === '0' ? 0 : busy === '1' ? 1 : -1 };
        }
        function append(item) {
            if (!item || closed || total >= 1024 || queue.length >= 128) return;
            queue.push(item);
            total++;
        }
        function receive(event) {
            if (!allowed() || performance.now() - started >= 600000) { stop(); return; }
            const target = field();
            if (!target || !(event.target instanceof Node) ||
                (event.target !== target && !target.contains(event.target))) return;
            append(sample(event.type, event.isTrusted === true));
        }
        function routeChanged() { if (!allowed()) stop(); }
        function stop() {
            if (closed) return;
            closed = true;
            queue = [];
            for (const type of types) document.removeEventListener(type, receive, true);
            window.removeEventListener('hashchange', routeChanged);
            window.removeEventListener('pagehide', stop);
            window.clearTimeout(expiry);
        }
        for (const type of types)
            document.addEventListener(type, receive, { capture: true, passive: true });
        window.addEventListener('hashchange', routeChanged);
        window.addEventListener('pagehide', stop);
        const expiry = window.setTimeout(stop, 600000);
        window[slot] = { stop, drain() {
            if (closed) return [];
            if (!allowed() || performance.now() - started >= 600000) { stop(); return []; }
            const item = sample('state', false);
            if (item) {
                const state = JSON.stringify({ ...item, elapsedMs: 0 });
                if (state !== previous) { previous = state; append(item); }
            }
            const result = queue;
            queue = [];
            return result;
        } };
    }
    return window[slot].drain();
}
