/*
 * JellyToneDown - theme music volume for the Jellyfin web client.
 *
 * This script is optional. The plugin's server-side gain already quietens theme media for
 * every client, including this one. What the script adds is a control the user can actually
 * reach: a small slider that appears while a theme is playing, and which saves the user's
 * own level back to the server.
 *
 * In "client script" mode the server deliberately serves this client untouched audio and
 * the scaling happens here instead, losslessly and instantly.
 *
 * It is written defensively on purpose: the Jellyfin web client has no plugin API, so
 * everything below is best-effort and must never break playback if an internal changes.
 */
(function () {
    'use strict';

    if (window.__jellyToneDownLoaded) {
        return;
    }
    window.__jellyToneDownLoaded = true;

    var LOG_PREFIX = '[JellyToneDown]';
    var SETTINGS_PATH = 'JellyToneDown/Volume';
    var UI_HIDE_DELAY_MS = 4500;
    var SAVE_DEBOUNCE_MS = 500;

    var settings = null;
    var themeItemIds = new Set();
    var trackedElements = new Set();
    var activeElement = null;
    var saveTimer = null;
    var hideTimer = null;
    var ui = null;

    function log() {
        if (!window.__jellyToneDownDebug) {
            return;
        }
        var args = Array.prototype.slice.call(arguments);
        args.unshift(LOG_PREFIX);
        console.debug.apply(console, args);
    }

    function clamp01(value) {
        if (!isFinite(value)) {
            return 1;
        }
        return Math.min(1, Math.max(0, value));
    }

    function amplitudeFor(percent) {
        var normalized = clamp01((Number(percent) || 0) / 100);
        if (settings && settings.Curve === 'Linear') {
            return normalized;
        }
        return Math.pow(normalized, 3);
    }

    /* ----------------------------------------------------------------------
     * Talking to the server
     * -------------------------------------------------------------------- */

    function apiClient() {
        return window.ApiClient && typeof window.ApiClient.getUrl === 'function'
            ? window.ApiClient
            : null;
    }

    function waitForApiClient() {
        return new Promise(function (resolve) {
            var attempts = 0;
            (function poll() {
                var client = apiClient();
                if (client && client.accessToken && client.accessToken()) {
                    resolve(client);
                    return;
                }
                if (++attempts > 120) {
                    resolve(null);
                    return;
                }
                setTimeout(poll, 500);
            })();
        });
    }

    function fetchSettings() {
        var client = apiClient();
        if (!client) {
            return Promise.resolve(null);
        }
        return client.ajax({
            type: 'GET',
            url: client.getUrl(SETTINGS_PATH),
            dataType: 'json'
        }).catch(function (err) {
            log('could not load settings', err);
            return null;
        });
    }

    function saveSettings(percent) {
        var client = apiClient();
        if (!client || !settings || !settings.UserOverridesAllowed) {
            return Promise.resolve(null);
        }
        return client.ajax({
            type: 'POST',
            url: client.getUrl(SETTINGS_PATH),
            contentType: 'application/json',
            data: JSON.stringify({ VolumePercent: percent }),
            dataType: 'json'
        }).catch(function (err) {
            log('could not save level', err);
            return null;
        });
    }

    function queueSave(percent) {
        if (saveTimer) {
            clearTimeout(saveTimer);
        }
        saveTimer = setTimeout(function () {
            saveTimer = null;
            saveSettings(percent).then(function (updated) {
                if (updated) {
                    settings = updated;
                }
                // When the server is doing the gain, the currently playing file was already
                // encoded at the old level. Reloading picks up the new one.
                if (settings && !settings.ClientSideScaling && activeElement) {
                    reloadElement(activeElement);
                }
            });
        }, SAVE_DEBOUNCE_MS);
    }

    function reloadElement(element) {
        try {
            var wasPlaying = !element.paused;
            var position = element.currentTime;
            element.load();
            if (wasPlaying) {
                element.currentTime = position || 0;
                var attempt = element.play();
                if (attempt && typeof attempt.catch === 'function') {
                    attempt.catch(function () { /* autoplay policy; harmless */ });
                }
            }
        } catch (err) {
            log('could not reload theme element', err);
        }
    }

    /* ----------------------------------------------------------------------
     * Learning which items are theme media
     *
     * The web client asks the server for theme media before it plays any, so watching
     * those responses tells us exactly which item IDs count as themes. Both fetch and
     * XMLHttpRequest are covered because the client has used each at different times.
     * -------------------------------------------------------------------- */

    var THEME_ENDPOINT = /\/Items\/[^/]+\/Theme(Media|Songs|Videos)/i;

    function rememberThemeIds(payload) {
        if (!payload || typeof payload !== 'object') {
            return;
        }

        var buckets = [
            payload.ThemeSongsResult,
            payload.ThemeVideosResult,
            payload.SoundtrackSongsResult,
            payload
        ];

        buckets.forEach(function (bucket) {
            if (!bucket || !Array.isArray(bucket.Items)) {
                return;
            }
            bucket.Items.forEach(function (item) {
                if (item && item.Id) {
                    themeItemIds.add(String(item.Id).replace(/-/g, '').toLowerCase());
                }
            });
        });

        // Keep the set from growing without bound on a long browsing session.
        if (themeItemIds.size > 2000) {
            themeItemIds = new Set(Array.from(themeItemIds).slice(-1000));
        }
    }

    function hookFetch() {
        if (typeof window.fetch !== 'function') {
            return;
        }
        var original = window.fetch;
        window.fetch = function (input, init) {
            var url = typeof input === 'string' ? input : (input && input.url) || '';
            var promise = original.apply(this, arguments);
            if (THEME_ENDPOINT.test(url)) {
                promise.then(function (response) {
                    try {
                        response.clone().json().then(rememberThemeIds).catch(function () { });
                    } catch (err) { /* opaque or already consumed */ }
                }).catch(function () { });
            }
            return promise;
        };
    }

    function hookXhr() {
        var open = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function (method, url) {
            try {
                if (THEME_ENDPOINT.test(String(url))) {
                    this.addEventListener('load', function () {
                        try {
                            var body = this.responseType === '' || this.responseType === 'text'
                                ? JSON.parse(this.responseText)
                                : this.response;
                            rememberThemeIds(body);
                        } catch (err) { /* not JSON */ }
                    });
                }
            } catch (err) { /* never block the request */ }
            return open.apply(this, arguments);
        };
    }

    function isThemeSource(src) {
        if (!src) {
            return false;
        }
        var match = String(src).match(/\/(?:Audio|Videos)\/([0-9a-fA-F-]{32,36})\//);
        if (!match) {
            return false;
        }
        return themeItemIds.has(match[1].replace(/-/g, '').toLowerCase());
    }

    /* ----------------------------------------------------------------------
     * Scaling the element's volume
     *
     * The proxy below keeps element.volume reporting the value the web client set, so its
     * own "remember my volume" logic keeps storing the user's real player volume, while
     * what actually reaches the speakers is scaled.
     * -------------------------------------------------------------------- */

    function attachProxy(element) {
        if (element.__jtdState) {
            return element.__jtdState;
        }

        var descriptor = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'volume');
        if (!descriptor || typeof descriptor.get !== 'function' || typeof descriptor.set !== 'function') {
            return null;
        }

        var state = {
            logical: descriptor.get.call(element),
            scale: 1,
            apply: function () {
                try {
                    descriptor.set.call(element, clamp01(state.logical * state.scale));
                } catch (err) {
                    log('could not apply scaled volume', err);
                }
            },
            detach: function () {
                state.scale = 1;
                try {
                    delete element.volume;
                    descriptor.set.call(element, clamp01(state.logical));
                } catch (err) { /* leave it be */ }
                delete element.__jtdState;
            }
        };

        try {
            Object.defineProperty(element, 'volume', {
                configurable: true,
                enumerable: true,
                get: function () {
                    return state.logical;
                },
                set: function (value) {
                    state.logical = clamp01(value);
                    state.apply();
                }
            });
        } catch (err) {
            log('could not install volume proxy', err);
            return null;
        }

        element.__jtdState = state;
        return state;
    }

    function applyToElement(element) {
        if (!settings) {
            return;
        }

        var isTheme = isThemeSource(element.currentSrc || element.src);

        if (isTheme) {
            activeElement = element;
            showUi();
        } else if (activeElement === element) {
            activeElement = null;
            hideUi();
        }

        if (!settings.ClientSideScaling) {
            // Server-side gain mode: the audio arriving is already at the right level.
            return;
        }

        var state = attachProxy(element);
        if (!state) {
            return;
        }

        state.scale = isTheme ? amplitudeFor(settings.VolumePercent) : 1;
        state.apply();
    }

    function trackElement(element) {
        if (!(element instanceof HTMLMediaElement) || trackedElements.has(element)) {
            return;
        }
        trackedElements.add(element);

        ['loadstart', 'loadedmetadata', 'play', 'playing'].forEach(function (name) {
            element.addEventListener(name, function () {
                applyToElement(element);
            });
        });

        ['ended', 'emptied', 'pause'].forEach(function (name) {
            element.addEventListener(name, function () {
                if (activeElement === element && (name === 'ended' || name === 'emptied')) {
                    activeElement = null;
                    hideUi();
                }
            });
        });

        applyToElement(element);
    }

    function watchForMediaElements() {
        // Capture phase catches loadstart even though media events do not bubble.
        document.addEventListener('loadstart', function (event) {
            if (event.target instanceof HTMLMediaElement) {
                trackElement(event.target);
            }
        }, true);

        // Belt and braces: some elements are created and played without ever being
        // attached where the capturing listener can see them.
        var play = HTMLMediaElement.prototype.play;
        HTMLMediaElement.prototype.play = function () {
            try {
                trackElement(this);
            } catch (err) { /* never block playback */ }
            return play.apply(this, arguments);
        };

        document.querySelectorAll('audio, video').forEach(trackElement);
    }

    /* ----------------------------------------------------------------------
     * The control itself
     * -------------------------------------------------------------------- */

    var STYLE = [
        '.jtd-panel{position:fixed;right:1rem;bottom:1rem;z-index:2000;display:flex;align-items:center;',
        'gap:.6rem;padding:.55rem .9rem;border-radius:2rem;background:rgba(24,24,24,.92);color:#fff;',
        'font:500 .82rem/1.2 inherit;box-shadow:0 .35rem 1.25rem rgba(0,0,0,.45);opacity:0;',
        'transform:translateY(.5rem);transition:opacity .18s ease,transform .18s ease;pointer-events:none;}',
        '.jtd-panel.jtd-visible{opacity:1;transform:none;pointer-events:auto;}',
        '.jtd-panel button{background:none;border:0;color:inherit;cursor:pointer;padding:0;line-height:0;}',
        '.jtd-panel input[type=range]{width:7.5rem;accent-color:#00a4dc;cursor:pointer;}',
        '.jtd-value{min-width:2.5rem;text-align:right;font-variant-numeric:tabular-nums;opacity:.85;}',
        '.jtd-label{opacity:.7;white-space:nowrap;}',
        '@media (max-width:600px){.jtd-panel input[type=range]{width:5rem;}.jtd-label{display:none;}}'
    ].join('');

    var SPEAKER_ON = '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">'
        + '<path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4z"/></svg>';
    var SPEAKER_OFF = '<svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">'
        + '<path d="M3 9v6h4l5 5V4L7 9H3zm18.3-1.3-1.4-1.4L17 9.2l-2.9-2.9-1.4 1.4L15.6 10.6l-2.9 2.9 1.4 1.4 2.9-2.9 2.9 2.9 1.4-1.4-2.9-2.9 2.9-2.9z"/></svg>';

    function buildUi() {
        if (ui) {
            return ui;
        }

        var style = document.createElement('style');
        style.textContent = STYLE;
        document.head.appendChild(style);

        var panel = document.createElement('div');
        panel.className = 'jtd-panel';
        panel.setAttribute('role', 'group');
        panel.setAttribute('aria-label', 'Theme music volume');

        var muteButton = document.createElement('button');
        muteButton.type = 'button';
        muteButton.title = 'Mute theme music';
        muteButton.setAttribute('aria-label', 'Mute theme music');
        muteButton.innerHTML = SPEAKER_ON;

        var label = document.createElement('span');
        label.className = 'jtd-label';
        label.textContent = 'Theme';

        var slider = document.createElement('input');
        slider.type = 'range';
        slider.min = '0';
        slider.max = '100';
        slider.step = '1';
        slider.setAttribute('aria-label', 'Theme music volume');

        var value = document.createElement('span');
        value.className = 'jtd-value';

        panel.append(muteButton, label, slider, value);
        document.body.appendChild(panel);

        var lastNonZero = 40;

        function render(percent) {
            slider.value = String(percent);
            value.textContent = percent + '%';
            muteButton.innerHTML = percent === 0 ? SPEAKER_OFF : SPEAKER_ON;
            muteButton.title = percent === 0 ? 'Restore theme music' : 'Mute theme music';
        }

        function change(percent, save) {
            percent = Math.min(100, Math.max(0, Math.round(percent)));
            if (percent > 0) {
                lastNonZero = percent;
            }
            if (settings) {
                settings.VolumePercent = percent;
            }
            render(percent);

            if (settings && settings.ClientSideScaling && activeElement && activeElement.__jtdState) {
                activeElement.__jtdState.scale = amplitudeFor(percent);
                activeElement.__jtdState.apply();
            }

            if (save) {
                queueSave(percent);
            }
        }

        slider.addEventListener('input', function () {
            change(Number(slider.value), true);
            keepVisible();
        });

        muteButton.addEventListener('click', function () {
            change(Number(slider.value) === 0 ? lastNonZero : 0, true);
            keepVisible();
        });

        ['pointerenter', 'pointermove', 'focusin'].forEach(function (name) {
            panel.addEventListener(name, keepVisible);
        });

        panel.addEventListener('pointerleave', scheduleHide);

        ui = { panel: panel, render: render, slider: slider };
        return ui;
    }

    function keepVisible() {
        if (hideTimer) {
            clearTimeout(hideTimer);
            hideTimer = null;
        }
    }

    function scheduleHide() {
        keepVisible();
        hideTimer = setTimeout(function () {
            hideUi();
        }, UI_HIDE_DELAY_MS);
    }

    function showUi() {
        if (!settings || !settings.UserOverridesAllowed) {
            return;
        }
        var built = buildUi();
        built.render(Math.min(100, Math.max(0, settings.VolumePercent)));
        built.panel.classList.add('jtd-visible');
        scheduleHide();
    }

    function hideUi() {
        keepVisible();
        if (ui) {
            ui.panel.classList.remove('jtd-visible');
        }
    }

    /* ----------------------------------------------------------------------
     * Start up
     * -------------------------------------------------------------------- */

    hookFetch();
    hookXhr();

    function start() {
        watchForMediaElements();

        waitForApiClient().then(function (client) {
            if (!client) {
                log('no authenticated ApiClient; standing down');
                return;
            }
            return fetchSettings().then(function (loaded) {
                if (!loaded) {
                    return;
                }
                settings = loaded;
                log('settings loaded', settings);
                trackedElements.forEach(applyToElement);
            });
        });

        // The user may sign out and back in as somebody else; refresh the level on
        // navigation so the panel does not show the previous account's setting.
        window.addEventListener('popstate', function () {
            fetchSettings().then(function (loaded) {
                if (loaded) {
                    settings = loaded;
                }
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
