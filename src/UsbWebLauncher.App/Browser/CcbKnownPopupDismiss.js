(targetWindow) => {
    'use strict';
    if (targetWindow === undefined) targetWindow = window;
    const document = targetWindow.document;
    const eligible = () => {
        try {
            const current = new URL(targetWindow.location.href);
            const top = new URL(targetWindow.top.location.href);
            return targetWindow !== targetWindow.top && current.protocol === 'https:' &&
                (current.port === '' || current.port === '443') &&
                !current.username && !current.password && !top.username && !top.password &&
                (current.hostname === 'b2b3.ccb.com' || current.hostname === 'xc.b2b3.ccb.com') &&
                current.pathname.startsWith('/NCCB/') &&
                top.origin === current.origin && top.pathname === '/NCCB/V6/b2bindex.jsp';
        } catch (_) { return false; }
    };
    if (!eligible()) return 'unsupported';
    const key = '__usbWebLauncherKnownPopupDismissV1';
    if (document[key]) return 'already-installed';
    try {
        const attempted = new WeakSet();
        let active = false;
        let scheduled = false;
        let installationReported = false;
        const report = message => {
            try {
                targetWindow.top.chrome.webview.postMessage(message);
                return;
            } catch (_) { }
            try { targetWindow.chrome.webview.postMessage(message); } catch (_) { }
        };
        const one = (root, selector) => {
            const matches = root.querySelectorAll(selector);
            return matches.length === 1 ? matches[0] : null;
        };
        const visible = element => {
            if (!element || !element.isConnected || !element.getClientRects().length) return false;
            for (let node = element; node; node = node.parentElement) {
                const style = node.ownerDocument.defaultView.getComputedStyle(node);
                if (node.hidden || style.display === 'none' || style.visibility === 'hidden' ||
                    style.visibility === 'collapse' || style.opacity === '0') return false;
            }
            return true;
        };
        const usable = element => element && !element.hasAttribute('disabled') &&
            element.getAttribute('aria-disabled') !== 'true';
        const clickKnown = (panel, button, event, imageMapArea = false) => {
            if (!visible(panel)) {
                attempted.delete(button);
                return;
            }
            if (!usable(button) || (!imageMapArea && !visible(button)) || attempted.has(button)) return;
            attempted.add(button);
            button.click();
            report(event);
            // Re-arm only after the website actually hides its panel. A handler
            // which merely mutates attributes cannot cause an observer click loop.
            if (!visible(panel)) attempted.delete(button);
        };
        const staticImage = (image, pathname) => {
            try {
                const source = new URL(image.src);
                return source.protocol === 'https:' && source.hostname === 'b2bstatic3.ccb.com' &&
                    source.port === '' && !source.username && !source.password && source.pathname === pathname;
            } catch (_) { return false; }
        };
        const dismissNotice = () => {
            try {
                const notice = one(document, '#noticeTip');
                if (!notice || notice.tagName !== 'DIV' || !notice.classList.contains('ccbWin')) return;
                const border = one(notice, '.ccbWinBoder');
                const head = border && one(border, '.ccbWinHead');
                const title = head && one(head, 'span');
                const button = head && one(head, 'a.ccbWinColse');
                if (!title || title.textContent.replace(/\s+/g, '') !== '温馨提示' ||
                    !button) return;
                // Use the website's own close handler to clear its corresponding UI state.
                clickKnown(notice, button, 'usb-launcher-popup:notice-clicked');
            } catch (_) { /* A disappearing or unsupported control does not block the page. */ }
        };
        const mobileCloseImage = image => {
            try {
                const source = new URL(image.src);
                return source.protocol === 'https:' && source.hostname === 'b2bstatic3.ccb.com' &&
                    source.port === '' && !source.username && !source.password &&
                    source.pathname === '/V6/STY6/CN/images7/FN90001/mob_drain_close.png';
            } catch (_) { return false; }
        };
        const dismissMobile = () => {
            try {
                // The bank uses .mob_drain for both its empty mask and its content.
                // Identify the content independently of any notice window or checkbox.
                const panels = [...document.querySelectorAll('div.mob_drain')]
                    .filter(panel => panel.querySelector('#top_sjyl_type1'));
                if (panels.length !== 1 || !one(panels[0], '#top_sjyl_type1')) return;
                const buttons = [...panels[0].querySelectorAll('img')].filter(mobileCloseImage);
                if (buttons.length !== 1) return;
                const button = buttons[0];
                // Its original handler owns both the promotion and the matching mask.
                clickKnown(panels[0], button, 'usb-launcher-popup:mobile-clicked');
            } catch (_) { /* Other known popups and business actions remain independent. */ }
        };
        const dismissGuide = () => {
            try {
                const panel = one(document, 'div#show1');
                const content = panel && one(panel, ':scope > .area_content');
                if (!content || !one(panel, 'div#background')) return;
                for (const step of [1, 2, 3]) {
                    const picture = one(content, '.head_img img#Newstep' + step);
                    const map = one(content, 'map#Map' + step);
                    if (!picture || !map || picture.getAttribute('usemap') !== '#Map' + step ||
                        map.getAttribute('name') !== 'Map' + step ||
                        !staticImage(picture, '/V6/STY6/CN/images6/FN000F1/Newstep' + (step - 1) + '.png')) continue;
                    const closes = [...map.querySelectorAll('area')].filter(area =>
                        /^\s*closeMengBan\s*\(\s*\)\s*;?\s*$/.test(area.getAttribute('onclick') || ''));
                    if (closes.length !== 1) continue;
                    // AREA elements have no rectangles; the associated picture
                    // carries image-map visibility, and only the close area is used.
                    if (!visible(panel)) attempted.delete(closes[0]);
                    else if (visible(picture)) clickKnown(panel, closes[0], 'usb-launcher-popup:guide-clicked', true);
                }
            } catch (_) { /* Unknown image maps remain untouched. */ }
        };
        const dismissPromotion = () => {
            try {
                const panel = one(document, 'div#myModal.modal');
                const content = panel && one(panel, ':scope > .modal-content');
                if (!content) return;
                const pictures = [...content.querySelectorAll('img')].filter(image =>
                    staticImage(image, '/V6/STY6/CN/images6/login_new/jtkd_image_png@1x.png'));
                const close = one(content, 'span#closeModal.closeModal');
                if (pictures.length !== 1 || !close) return;
                if (!visible(panel)) attempted.delete(close);
                else if (visible(pictures[0])) clickKnown(panel, close, 'usb-launcher-popup:promotion-clicked');
            } catch (_) { /* Generic modals and transaction confirmations are not supported. */ }
        };
        const scan = () => {
            if (!active || !eligible()) return;
            try {
                let frameWindow = targetWindow;
                let depth = 0;
                while (frameWindow !== targetWindow.top && depth++ < 8) {
                    if (!visible(frameWindow.frameElement)) return;
                    frameWindow = frameWindow.parent;
                }
                if (frameWindow !== targetWindow.top) return;
            } catch (_) { return; }
            dismissNotice();
            dismissMobile();
            dismissGuide();
            dismissPromotion();
        };
        const scheduleScan = () => {
            if (!active || scheduled) return;
            scheduled = true;
            targetWindow.queueMicrotask(() => {
                scheduled = false;
                scan();
            });
        };
        const observer = new targetWindow.MutationObserver(scheduleScan);
        const start = () => {
            if (active || !eligible()) return;
            active = true;
            observer.observe(document, {
                childList: true, subtree: true, attributes: true,
                attributeFilter: ['style', 'class', 'hidden', 'id', 'src', 'usemap', 'name', 'onclick', 'disabled', 'aria-disabled']
            });
            if (!installationReported) {
                installationReported = true;
                report('usb-launcher-popup:notice-installed');
            }
            // Defer the initial scan and coalesce it with pending DOM mutations.
            scheduleScan();
        };
        const stop = () => {
            active = false;
            observer.disconnect();
            document.removeEventListener('DOMContentLoaded', start);
        };
        document[key] = true;
        targetWindow.addEventListener('pagehide', stop);
        targetWindow.addEventListener('pageshow', event => { if (event.persisted) start(); });
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
        else start();
        return 'installed';
    } catch (_) { return 'failed'; }
}
