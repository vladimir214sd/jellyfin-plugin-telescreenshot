/**
 * TeleScreenshot — frontend logic injected into every jellyfin-web page.
 *
 * Responsibilities:
 *   1. Watch for the video player (`video.htmlvideoplayer`) and its OSD to appear (the player
 *      is an SPA route, so we use a MutationObserver instead of a page-load event).
 *   2. Inject a "Take Screenshot" button into the OSD controls.
 *   3. On click, capture the current frame to a canvas -> PNG blob -> base64, then POST it to
 *      the backend endpoint `/TeleScreenshot/Send`, which forwards it to Telegram.
 *
 * Defensive coding notes:
 *   - jellyfin-web globals (e.g. `toast`, `playbackManager`, `ApiClient`) are accessed via the
 *     AMD loader `require(...)` where possible, and always wrapped in try/catch with a fallback.
 *   - All selectors are version-tolerant; if a container is missing we simply skip injection.
 */
(function () {
    'use strict';

    var BUTTON_ID = 'telescreenshot-btn';
    var VIDEO_SELECTOR = 'video.htmlvideoplayer';
    // OSD buttons live in `.osdControls`; individual control rows are `.osdControls .btn`. We
    // append our own button and let CSS position it; selector is intentionally lenient.
    var OSD_CONTAINER_SELECTOR = '.videoOsdInner, .osdInner, .osdControls';

    // Plugin config is cached after the first successful fetch so we can hide the button when
    // the plugin is disabled without re-fetching on every mutation.
    var cachedConfig = null;
    var configFetchInFlight = false;

    /**
     * Show a toast message, preferring jellyfin-web's `toast` module and falling back to a
     * minimal DOM toast if the module is unavailable (e.g. on non-standard builds).
     */
    function toast(message, severity) {
        var opts = { text: message };
        if (severity === 'error') opts.type = 'error';
        if (severity === 'success') opts.type = 'success';
        try {
            if (typeof require !== 'undefined') {
                require(['toast'], function (toastModule) {
                    if (typeof toastModule === 'function') {
                        toastModule(opts);
                    } else if (toastModule && typeof toastModule.default === 'function') {
                        toastModule.default(opts);
                    } else {
                        fallbackToast(message);
                    }
                });
                return;
            }
        } catch (e) { /* fall through */ }
        fallbackToast(message);
    }

    function fallbackToast(message) {
        try {
            var el = document.createElement('div');
            el.textContent = message;
            Object.assign(el.style, {
                position: 'fixed', left: '50%', bottom: '10%',
                transform: 'translateX(-50%)', zIndex: 2147483647,
                background: 'rgba(0,0,0,0.85)', color: '#fff',
                padding: '10px 16px', borderRadius: '6px',
                font: '14px/1.4 sans-serif', maxWidth: '80vw', textAlign: 'center'
            });
            document.body.appendChild(el);
            setTimeout(function () { el.remove(); }, 3000);
        } catch (e) { /* nothing more we can do */ }
    }

    /**
     * Fetch the plugin config once and cache it. Returns a promise resolving to the config
     * object (possibly cached). Never rejects — on failure resolves to a permissive default.
     */
    function getConfig() {
        if (cachedConfig) return Promise.resolve(cachedConfig);
        if (configFetchInFlight) {
            // Another call is fetching; poll briefly for the result.
            return new Promise(function (resolve) {
                var tries = 0;
                var iv = setInterval(function () {
                    if (cachedConfig) { clearInterval(iv); resolve(cachedConfig); }
                    else if (++tries > 40) { clearInterval(iv); resolve(null); }
                }, 100);
            });
        }
        configFetchInFlight = true;
        return apiAjax({ url: 'TeleScreenshot/Config', type: 'GET' }).then(function (r) {
            cachedConfig = (r && r.body) || null;
            configFetchInFlight = false;
            return cachedConfig;
        }).catch(function () {
            configFetchInFlight = false;
            return null;
        });
    }

    /**
     * Resolve the active ApiClient. jellyfin-web keeps the default connection on `window.ApiClient`,
     * but some setups only expose it via the connectionManager module. Prefer ApiClient.ajax over
     * a raw fetch() because it builds the absolute URL and attaches the correct auth header
     * automatically — a hand-rolled Authorization header can throw a DOMException
     * ("The string did not match the expected pattern") before the request goes out.
     */
    function getApiClient() {
        try {
            if (window.ApiClient && typeof window.ApiClient.ajax === 'function') {
                return Promise.resolve(window.ApiClient);
            }
        } catch (e) { /* fall through */ }
        if (typeof require !== 'undefined') {
            return new Promise(function (resolve) {
                require(['connectionManager'], function (cm) {
                    var c = cm && (cm.currentApiClient ? cm.currentApiClient() : cm);
                    resolve(c && typeof c.ajax === 'function' ? c : null);
                }, function () { resolve(null); });
            });
        }
        return Promise.resolve(null);
    }

    /**
     * One-shot wrapper around ApiClient.ajax that resolves to a parsed JSON body (or null) plus
     * status. On non-2xx responses it rejects with an Error whose message includes the body.
     * Never throws a DOMException — auth is handled inside ApiClient.
     */
    function apiAjax(opts) {
        return getApiClient().then(function (client) {
            if (!client) {
                throw new Error('Jellyfin ApiClient not available.');
            }
            // ApiClient.getUrl normalises the path against the server's base URL.
            opts.url = client.getUrl(opts.url);
            return client.ajax(opts);
        }).then(function (result) {
            // ApiClient.ajax auto-parses JSON when the response content-type is JSON; otherwise
            // it returns text. Normalise both to an object.
            if (result && typeof result === 'object') return { status: 200, body: result };
            if (typeof result === 'string' && result.length) {
                try { return { status: 200, body: JSON.parse(result) }; }
                catch (e) { return { status: 200, body: null, raw: result }; }
            }
            return { status: 200, body: null };
        });
    }

    /**
     * Extract the currently-playing item id from the URL hash. jellyfin-web player route looks
     * like `#/video?id=<guid>`. Returns null if not found.
     */
    function getItemIdFromHash() {
        try {
            var hash = (location.hash || '');
            var q = hash.indexOf('?');
            if (q < 0) return null;
            var params = new URLSearchParams(hash.slice(q + 1));
            return params.get('id') || null;
        } catch (e) { return null; }
    }

    /**
     * Best-effort current position in ticks (100ns units) from the video element. jellyfin-web's
     * playbackManager is the authoritative source, but it is module-scoped; reading the video's
     * currentTime directly is robust across builds.
     */
    function getPositionTicks(video) {
        try {
            if (video && isFinite(video.currentTime) && video.currentTime > 0) {
                // 1 second = 10,000,000 ticks.
                return Math.round(video.currentTime * 10000000);
            }
        } catch (e) { /* ignore */ }
        return null;
    }

    /**
     * Capture the current video frame to a PNG data URL. Resolves to a base64 string
     * (without the `data:...;base64,` prefix). Rejects on tainted canvas (cross-origin media).
     */
    function captureFrame(video) {
        return new Promise(function (resolve, reject) {
            if (!video || !video.videoWidth || !video.videoHeight) {
                reject(new Error('No video frame available.'));
                return;
            }
            var canvas = document.createElement('canvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            var ctx = canvas.getContext('2d');
            if (!ctx) { reject(new Error('Canvas 2D context unavailable.')); return; }

            try {
                ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
            } catch (e) {
                reject(new Error('Could not draw video frame.'));
                return;
            }

            // toDataURL throws SecurityError on tainted canvas; catch and surface a clear message.
            var dataUrl;
            try {
                dataUrl = canvas.toDataURL('image/png');
            } catch (secErr) {
                reject(new Error('Frame capture blocked by the browser (tainted canvas / CORS).'));
                return;
            }
            var comma = dataUrl.indexOf(',');
            resolve(comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl);
        });
    }

    /**
     * Send a captured frame to the backend. Returns a promise resolving to the parsed response.
     */
    function sendScreenshot(imageBase64, itemId, positionTicks) {
        var payload = {
            itemId: itemId,
            positionTicks: positionTicks,
            imageBase64: imageBase64
        };
        return apiAjax({
            url: 'TeleScreenshot/Send',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload)
        }).then(function (r) {
            var ok = r && r.body && r.body.ok;
            var msg = (r && r.body && r.body.message) || 'sent';
            return { ok: ok, message: msg };
        }).catch(function (err) {
            var msg = (err && err.message) ? err.message : ('' + err);
            return { ok: false, message: msg };
        });
    }

    /**
     * Handle the screenshot button click.
     */
    function onScreenshotClick(video) {
        var itemId = getItemIdFromHash();
        var positionTicks = getPositionTicks(video);

        // Optional "captured" toast first, then attempt send.
        var config = cachedConfig || {};
        if (config.ShowCaptureToast !== false) {
            // We don't know yet if capture will succeed, so only show "sending…" after capture.
        }

        captureFrame(video).then(function (base64) {
            if (config.ShowCaptureToast !== false) {
                toast('Screenshot captured — sending to Telegram…');
            }
            return sendScreenshot(base64, itemId, positionTicks);
        }).then(function (result) {
            if (result.ok) {
                toast('Screenshot sent to Telegram.', 'success');
            } else {
                toast('Telegram send failed: ' + result.message, 'error');
            }
        }).catch(function (err) {
            toast('Screenshot failed: ' + (err && err.message ? err.message : err), 'error');
        });
    }

    /**
     * Build the button element. Idempotent: returns null if already present.
     */
    function buildButton(video) {
        if (document.getElementById(BUTTON_ID)) return null;

        var btn = document.createElement('button');
        btn.id = BUTTON_ID;
        btn.type = 'button';
        btn.className = 'telescreenshot-btn paper-icon-button-light';
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.setAttribute('title', 'Take screenshot (Telegram)');
        btn.setAttribute('aria-label', 'Take screenshot');

        // Inline SVG camera icon (no external dependency).
        btn.innerHTML =
            '<svg viewBox="0 0 24 24" width="100%" height="100%" aria-hidden="true">' +
            '<path fill="currentColor" d="M9 4l-1 2H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8' +
            'a2 2 0 0 0-2-2h-4l-1-2H9zm3 5a4 4 0 0 1 4 4a4 4 0 0 1-4 4a4 4 0 0 1-4-4a4 4 0 0 1 4-4zm0 2' +
            'a2 2 0 0 0-2 2a2 2 0 0 0 2 2a2 2 0 0 0 2-2a2 2 0 0 0-2-2z"/></svg>';

        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            onScreenshotClick(video);
        });

        return btn;
    }

    /**
     * Try to place the button into the OSD. Returns true if placed (or already present).
     */
    function tryInject() {
        var video = document.querySelector(VIDEO_SELECTOR);
        if (!video) return false;

        if (document.getElementById(BUTTON_ID)) {
            // Button exists; make sure it points at the current video element.
            var existing = document.getElementById(BUTTON_ID);
            existing._tsVideo = video;
            return true;
        }

        // Respect plugin config: hide button if disabled.
        getConfig().then(function (cfg) {
            if (cfg && cfg.Enabled === false) return; // plugin disabled, do not inject
            var container = document.querySelector(OSD_CONTAINER_SELECTOR);
            var btn = buildButton(video);
            if (!btn) return;
            btn._tsVideo = video;
            if (container) {
                container.appendChild(btn);
            } else {
                // No known OSD container — attach to body with absolute positioning as a fallback.
                document.body.appendChild(btn);
                btn.classList.add('telescreenshot-btn--floating');
            }
        });
        return true;
    }

    /**
     * Remove the button (e.g. when leaving the player).
     */
    function tryRetract() {
        if (document.querySelector(VIDEO_SELECTOR)) return; // still playing
        var btn = document.getElementById(BUTTON_ID);
        if (btn) btn.remove();
    }

    // ----- Bootstrap -----

    // MutationObserver: scan for the player/OSD appearing or disappearing as the user navigates.
    var scanTimer = null;
    function scheduleScan() {
        if (scanTimer) return;
        scanTimer = setTimeout(function () {
            scanTimer = null;
            if (document.querySelector(VIDEO_SELECTOR)) {
                tryInject();
            } else {
                tryRetract();
            }
        }, 150);
    }

    try {
        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.documentElement, { childList: true, subtree: true });
    } catch (e) {
        // MutationObserver unavailable — poll on an interval as a fallback.
        setInterval(scheduleScan, 1000);
    }

    // Also re-scan on SPA navigation events if jellyfin-web exposes them.
    try {
        if (typeof require !== 'undefined') {
            require(['events'], function (events) {
                // No public page-change event we can rely on; the MutationObserver covers it.
            });
        }
    } catch (e) { /* ignore */ }

    // Initial scan after load.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scheduleScan);
    } else {
        scheduleScan();
    }
})();
