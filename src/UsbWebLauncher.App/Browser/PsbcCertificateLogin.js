(request) => {
    const result = status => ({ status });
    const key = '__usbWebLauncherPsbcLoginV1';
    const normalized = text => (text || '').replace(/\s/g, '');
    const allowed = () => {
        const url = new URL(location.href);
        return window === window.top && url.origin === 'https://corpebank.psbc.com' &&
            !url.username && !url.password && url.pathname === '/' && !url.search &&
            ['#/login', '#/login?tab=ukeyLogin'].includes(url.hash);
    };
    const visible = element => {
        if (!element?.isConnected || element.getClientRects().length === 0) return false;
        for (let node = element; node; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (style.display === 'none' || ['hidden', 'collapse'].includes(style.visibility)) return false;
        }
        return true;
    };
    const fields = () => {
        if (!allowed()) return { status: 'unsupported' };
        const tab = document.querySelector('#tab-ukeyLogin');
        const panes = document.querySelectorAll('#pane-ukeyLogin');
        if (panes.length !== 1 || !visible(panes[0])) return { status: 'waiting' };
        const pane = panes[0];
        if (!visible(tab) || tab.getAttribute('aria-selected') !== 'true')
            return { status: 'unsupported' };
        const forms = pane.querySelectorAll('form');
        if (forms.length !== 1) return { status: 'waiting' };
        const form = forms[0];
        const password = form.querySelector('#ukeyLogin_div');
        const mask = form.querySelector('#ukeyLogin_input');
        if (!password || !mask) return { status: 'waiting' };
        if (document.querySelectorAll('#tab-ukeyLogin').length !== 1 ||
            document.querySelectorAll('#ukeyLogin_div').length !== 1 ||
            document.querySelectorAll('#ukeyLogin_input').length !== 1)
            return { status: 'unsupported' };
        if (!visible(password) || !visible(mask) || !password.contains(mask) ||
            password.getAttribute('tabindex') !== '0' ||
            normalized(password.closest('.vx-pwd')?.querySelector('.pwd-prefix span')?.textContent) !== '\u767b\u5f55\u5bc6\u7801')
            return { status: 'unsupported' };
        const accounts = Array.from(form.querySelectorAll('input')).filter(e => visible(e) && e.type === 'text');
        if (accounts.length !== 2) return { status: 'unsupported' };
        const labels = accounts.map(e => normalized(e.closest('.el-input')?.querySelector('.el-input__prefix')?.textContent));
        if (labels[0] !== '\u5ba2\u6237\u7f16\u53f7' || labels[1] !== '\u767b\u5f55\u540d')
            return { status: 'unsupported' };
        if (accounts.some(e => !e.value.trim())) return { status: 'waiting' };
        const buttons = Array.from(form.querySelectorAll('button.loginbtn')).filter(visible);
        if (buttons.length !== 1) return { status: 'unsupported' };
        const button = buttons[0];
        if (normalized(button.textContent) !== '\u767b\u5f55') return { status: 'unsupported' };
        if (button.disabled || button.classList.contains('is-loading') ||
            button.getAttribute('aria-disabled') === 'true' || window.g_input_connectFlag !== 1)
            return { status: 'waiting' };
        return { status: 'ready', tab, pane, form, password, mask, accounts, button };
    };
    try {
        const state = document[key];
        if (request.operation === 'release') {
            if (state?.ticket === request.ticket && state.started && document.activeElement === state.password)
                state.password.blur();
            return result('released');
        }
        const current = fields();
        if (current.status !== 'ready') return result(current.status);
        const { tab, pane, form, password, mask, accounts, button } = current;
        const count = () => /^[\u25cf*]*$/.test(mask.textContent) ? mask.textContent.length : -1;
        const unchanged = () => state && state.ticket === request.ticket && state.href === location.href &&
            allowed() && state.tab === tab && state.pane === pane && state.form === form &&
            state.password === password && state.mask === mask && state.button === button &&
            accounts.every((e, i) => e === state.accounts[i] && e.value === state.accountValues[i]);
        const focused = () => document.hasFocus() && document.activeElement === password &&
            password.getAttribute('isbusy') === '1' && window.g_stopSendRequest === 0;
        if (request.operation === 'inspect') {
            if (count() !== 0 || state?.started) return result('unsupported');
            document[key] = { ticket: request.ticket, href: location.href,
                tab, pane, form, password, mask, accounts, button,
                accountValues: accounts.map(e => e.value), started: false, submitted: false };
            return result('ready');
        }
        if (!unchanged() || state.submitted) return result('unsupported');
        if (request.operation === 'prepare') {
            if (state.started || count() !== 0 || !document.hasFocus()) return result('unsupported');
            state.started = true;
            password.focus(); // Activates the bank's own startSafeInput handler.
            return result(focused() ? 'focused' : 'failed');
        }
        if (!state.started || !focused() || !Number.isInteger(request.length) ||
            request.length < 6 || request.length > 20) return result('failed');
        const length = count();
        if (length < 0 || length > request.length) return result('failed');
        if (request.operation === 'check')
            return result(length === request.length ? 'filled' : length === 0 ? 'empty' : 'pending');
        if (request.operation !== 'submit' || length !== request.length) return result('failed');
        state.submitted = true;
        password.blur(); // Stops capture before the existing encryption/login handler.
        const checked = fields();
        if (!unchanged() || checked.status !== 'ready' || checked.password !== password ||
            checked.button !== button || checked.mask !== mask || checked.form !== form ||
            checked.tab !== tab || checked.pane !== pane ||
            !checked.accounts.every((e, i) => e === state.accounts[i] && e.value === state.accountValues[i]) ||
            !document.hasFocus() || count() !== request.length) return result('failed');
        button.click();
        return result('submitted');
    } catch {
        return result('failed');
    }
}
