(request) => {
    // Execute from the approved top-level page; inspect only visible, same-origin bank frames.
    const rootDocument = window.document;
    let document = rootDocument;
    const result = status => ({ status });
    const key = '__usbWebLauncherCertificateLoginV1';
    const visible = element => {
        if (!element || !element.isConnected || element.getClientRects().length === 0) return false;
        for (let node = element; node; node = node.parentElement) {
            const style = node.ownerDocument.defaultView.getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
        }
        return true;
    };
    const documents = () => {
        const found = [rootDocument];
        const visit = (parent, depth) => {
            if (depth >= 4 || found.length >= 16) return;
            for (const frame of parent.querySelectorAll('iframe,frame')) {
                if (found.length >= 16) break;
                if (!visible(frame)) continue;
                try {
                    const child = frame.contentDocument;
                    if (!child || !child.defaultView) continue;
                    const url = new URL(child.URL);
                    if (url.origin !== request.origin || !url.pathname.startsWith('/NCCB/')) continue;
                    found.push(child);
                    visit(child, depth + 1);
                } catch { /* Cross-origin frames are outside this login flow. */ }
            }
        };
        visit(rootDocument, 0);
        return found;
    };
    const normalized = text => (text || '').replace(/\s/g, '').replace(/[:：]$/, '');
    const loginPassword = field => field.type === 'password' &&
        (field.id === 'LOG_PWD' || field.name === 'LOG_PWD' ||
         [field.placeholder, field.getAttribute('aria-label'), ...Array.from(field.labels || [], label => label.textContent)]
             .some(text => normalized(text) === '登录密码'));
    const allowedLocation = () => {
        const url = new URL(location.href);
        if (window !== window.top || url.origin !== request.origin || url.pathname !== request.path) return false;
        if (url.pathname === '/NCCB/MNECV6B2BMainPlat_UN') {
            return url.searchParams.getAll('TXCODE').length === 1 && url.searchParams.get('TXCODE') === 'N7N010' &&
                url.searchParams.getAll('LOGIN_TYPE').length === 1 && url.searchParams.get('LOGIN_TYPE') === 'UC';
        }
        return url.pathname === '/NCCB/V6/b2bindex.jsp';
    };
    if (request.operation === 'diagnose') {
        if (!allowedLocation()) return result('unsupported');
        const address = raw => {
            try { const u = new URL(raw, location.href); return { origin: u.origin, path: u.pathname }; }
            catch { return null; }
        };
        return {
            status: 'diagnostic', readyState: rootDocument.readyState,
            controls: documents().flatMap(doc => Array.from(doc.querySelectorAll('input:not([type="hidden"]),select,button'))
                .slice(0, 40).map(e => ({ tag: e.tagName, id: e.id, name: e.name, type: e.type,
                    placeholder: e.placeholder, visible: visible(e), disabled: !!e.disabled,
                    readOnly: !!e.readOnly, formId: e.form?.id || '', formName: e.form?.name || '',
                    document:address(doc.URL) }))),
            forms: documents().flatMap(doc => Array.from(doc.forms).slice(0, 12).map(e => ({id:e.id,name:e.name,method:e.method,
                action:address(e.action),document:address(doc.URL),certificateHeading:e.textContent.replace(/\s/g,'').includes('证书登录')}))),
            frames: documents().flatMap(doc => Array.from(doc.querySelectorAll('iframe,frame')).slice(0, 12)
                .map(e => ({id:e.id,address:address(e.src),visible:visible(e)})))
        };
    }
    const fields = () => {
        if (!allowedLocation()) return { status: 'unsupported' };
        // Driver/account variants can hide identity fields and rename forms/controls.
        // Select the active login by its purpose, not a particular driver's template.
        const candidates = documents().flatMap(doc => Array.from(doc.querySelectorAll('input'))
            .filter(field => visible(field) && loginPassword(field) && field.closest('form')));
        if (candidates.length !== 1) return { status: candidates.length > 1 ? 'unsupported' : 'waiting' };
        const password = candidates[0];
        document = password.ownerDocument;
        const form = password.closest('form');
        const loginActions = selector => Array.from(form.querySelectorAll(selector))
            .filter(e => visible(e) && normalized(e.value || e.textContent) === '登录');
        // Help/navigation links may also say "登录". Prefer actual buttons before
        // considering link-based templates; never treat those links as extra submits.
        let buttons = loginActions('input[type="button"],input[type="submit"],button');
        if (buttons.length === 0) buttons = loginActions('[role="button"]');
        if (buttons.length === 0) buttons = loginActions('a');
        if (buttons.length !== 1) return { status: buttons.length > 1 ? 'unsupported' : 'waiting' };
        const button = buttons[0];
        // Account identity is optional after certificate selection. When present, keep
        // tracking it so a page event cannot silently switch accounts before submission.
        const identifiedAccount = e => e.id === 'CUSTOMERID' || e.name === 'CUSTOMERID' ||
            e.id === 'USERID' || e.name === 'USERID' ||
            [e.placeholder, e.getAttribute('aria-label'), ...Array.from(e.labels || [], label => label.textContent)]
                .some(text => /^(客户识别号|操作员代码)$/.test(normalized(text)));
        const accounts = Array.from(form.querySelectorAll('input,select')).filter(e =>
            e.type !== 'password' && (identifiedAccount(e) ||
            (visible(e) && (e.tagName === 'SELECT' || ['text', 'tel', 'email', 'number'].includes(e.type)))));
        const action = new URL(form.action, location.href);
        if (form.method.toLowerCase() !== 'post' ||
            action.origin !== location.origin ||
            !/^\/NCCB\/(?:MNECV6B2BMainPlat_UN|NECV6B2BMainPlat(?:_\d{2})?)$/.test(action.pathname) ||
            password.form !== form || button.closest('form') !== form ||
            ('form' in button && button.form !== form) ||
            accounts.some(account => account.form !== form)) return { status: 'unsupported' };
        if (password.disabled || password.readOnly || accounts.some(account =>
            identifiedAccount(account) && visible(account) && !account.value.trim()))
            return { status: 'waiting' };
        return { status: 'ready', form, password, button, accounts };
    };
    let filled = null;
    let clickStarted = false;
    try {
        const current = fields();
        if (current.status !== 'ready') return result(current.status);
        const { form, password, button, accounts } = current;
        if (request.operation === 'inspect') {
            if (password.value !== '') return result('unsupported');
            const previous = document[key];
            if (previous?.ticket === request.ticket && previous.consumed) return result('unsupported');
            document[key] = { ticket: request.ticket, form, password, button, accounts,
                accountValues: accounts.map(account => account.value), consumed: false };
            return result('ready');
        }
        const state = document[key];
        const unchanged = () => state && state.ticket === request.ticket && allowedLocation() &&
            document === password.ownerDocument && documents().includes(document) &&
            state.form === form && state.password === password && state.button === button &&
            state.accounts.length === accounts.length &&
            accounts.every((account, index) => state.accounts[index] === account && account.value === state.accountValues[index]) &&
            [form, password, button, ...accounts].every(e => e.isConnected);
        if (request.operation !== 'submit' || !unchanged() || state.consumed || password.value !== '' ||
            typeof request.pin !== 'string' || request.pin.length === 0) return result('unsupported');
        // Consume before any event dispatch: page callbacks may re-enter or navigate.
        state.consumed = true;
        const realm = password.ownerDocument.defaultView;
        const setter = Object.getOwnPropertyDescriptor(realm.HTMLInputElement.prototype, 'value').set;
        setter.call(password, request.pin);
        filled = password;
        password.dispatchEvent(new realm.Event('input', { bubbles: true }));
        password.dispatchEvent(new realm.Event('change', { bubbles: true }));
        const checked = fields();
        if (!unchanged() || checked.status !== 'ready' || checked.password !== password ||
            checked.button !== button || checked.accounts.length !== accounts.length ||
            checked.accounts.some((account, index) => account !== accounts[index]) ||
            button.disabled || button.getAttribute('aria-disabled') === 'true' ||
            button.classList.contains('btn_disabled') || password.value !== request.pin) {
            setter.call(password, '');
            password.dispatchEvent(new realm.Event('input', { bubbles: true }));
            return result('failed');
        }
        clickStarted = true;
        button.click(); // Keep CCB's encryption, validation and existing submit handler.
        return result('submitted');
    } catch {
        if (filled && !clickStarted) {
            try { Object.getOwnPropertyDescriptor(filled.ownerDocument.defaultView.HTMLInputElement.prototype, 'value').set.call(filled, ''); } catch { }
        }
        return result('failed');
    }
}
