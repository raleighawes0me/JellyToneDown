/*
 * Behavioural tests for the theme volume panel in jellytonedown.js.
 *
 * Why this exists: nothing else in this repository can be tested outside CI. The C# needs
 * Jellyfin's NuGet packages and a running server, so the only thing that can be proved on a
 * developer's machine is the browser script - and that is also the part with the fiddliest
 * behaviour, because it hangs off media events and timers rather than anything you can call
 * directly.
 *
 * The script is loaded unmodified. Nothing here reaches into its internals; the tests drive
 * the same events a browser would fire and assert on what ends up in the DOM. The clock is
 * faked so a four-second timeout does not cost four seconds.
 *
 * Three things about jsdom cost time to work out, so they are worth stating plainly:
 *
 *   1. The JSDOM needs a real `url`. Against the default about:blank, XMLHttpRequest.open()
 *      on a relative path throws a SyntaxError, and the script hooks XHR.
 *   2. The script only learns which item IDs are themes by watching Items/.../ThemeMedia
 *      responses go past, so a test has to drive that hook before any element looks like a
 *      theme to it.
 *   3. jsdom has no playback, so `paused` and `ended` never change on their own. They are
 *      backed here by test-controlled getters, set alongside the event that a real browser
 *      would have fired for the same transition.
 *
 * Run with:  cd tests && npm install && npm test
 */
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const SCRIPT_PATH = path.resolve(
    __dirname, '..', 'Jellyfin.Plugin.JellyToneDown', 'Web', 'jellytonedown.js');
const SCRIPT = fs.readFileSync(SCRIPT_PATH, 'utf8');

const THEME_ID = '11111111111111111111111111111111';

// Must match UI_HIDE_DELAY_MS in the script.
const HIDE_DELAY = 4500;

function setup() {
    // A real URL, or XMLHttpRequest.open() on a relative path throws against about:blank.
    const dom = new JSDOM('<!DOCTYPE html><html><head></head><body></body></html>', {
        runScripts: 'outside-only',
        pretendToBeVisual: false,
        url: 'http://localhost:8096/web/index.html'
    });
    const win = dom.window;

    // --- a clock we control -------------------------------------------------
    let now = 0;
    let seq = 1;
    const timers = new Map();
    win.setTimeout = (fn, ms) => {
        const id = seq++;
        timers.set(id, { fn, at: now + (ms || 0) });
        return id;
    };
    win.clearTimeout = (id) => { timers.delete(id); };
    const advance = (ms) => {
        now += ms;
        [...timers.entries()]
            .filter(([, t]) => t.at <= now)
            .sort((a, b) => a[1].at - b[1].at)
            .forEach(([id, t]) => { timers.delete(id); t.fn(); });
    };

    // --- a media element we can drive ---------------------------------------
    const media = win.document.createElement('audio');
    Object.defineProperty(media, 'currentSrc', {
        value: `/Audio/${THEME_ID}/universal`, writable: true });
    let paused = true;
    let ended = false;
    Object.defineProperty(media, 'paused', { get: () => paused });
    Object.defineProperty(media, 'ended', { get: () => ended });
    win.document.body.appendChild(media);

    // --- the smallest ApiClient the script will accept ----------------------
    win.ApiClient = {
        accessToken: () => 'token',
        getUrl: (p) => '/' + p,
        ajax: () => Promise.resolve({
            VolumePercent: 40,
            IsUserOverride: true,
            ServerDefaultPercent: 40,
            UserOverridesAllowed: true,
            ClientSideScaling: false,
            Curve: 'Cubic'
        })
    };

    win.eval(SCRIPT);

    return {
        win,
        advance,
        fire: (name) => media.dispatchEvent(new win.Event(name)),
        play: () => { paused = false; ended = false; },
        pause: () => { paused = true; },
        end: () => { paused = true; ended = true; },

        // Teach the script that THEME_ID is a theme, the way a real response would: through
        // the XMLHttpRequest hook it installed over the prototype.
        rememberTheme: () => {
            const xhr = new win.XMLHttpRequest();
            xhr.open('GET', '/Items/abc/ThemeMedia');
            Object.defineProperty(xhr, 'responseText', {
                value: JSON.stringify({ ThemeSongsResult: { Items: [{ Id: THEME_ID }] } }) });
            Object.defineProperty(xhr, 'responseType', { value: '' });
            xhr.dispatchEvent(new win.Event('load'));
        },

        panel: () => win.document.querySelector('.jtd-panel'),
        visible: () => {
            const p = win.document.querySelector('.jtd-panel');
            return !!p && p.classList.contains('jtd-visible');
        }
    };
}

const results = [];
function check(name, actual, expected) {
    const ok = actual === expected;
    results.push({ name, ok });
    console.log(`${ok ? 'ok  ' : 'FAIL'}  ${name}${ok ? '' : `  (got ${actual}, want ${expected})`}`);
}

(async function run() {
    const t = setup();
    t.rememberTheme();

    // Let the script's settings promise settle before anything is expected of it.
    await new Promise((r) => setImmediate(r));
    await new Promise((r) => setImmediate(r));

    // --- the panel is tied to playback, not to a timer ----------------------

    t.play();
    t.fire('playing');
    check('appears when a theme starts playing', t.visible(), true);

    t.advance(HIDE_DELAY * 4);
    check('stays up while the theme keeps playing', t.visible(), true);

    t.panel().dispatchEvent(new t.win.Event('pointerleave'));
    t.advance(HIDE_DELAY * 2);
    check('pointerleave does not hide it mid-theme', t.visible(), true);

    // --- stopping starts the countdown, it does not snatch it away ----------

    t.pause();
    t.fire('pause');
    check('still visible immediately after a pause', t.visible(), true);

    t.advance(HIDE_DELAY - 1);
    check('still visible just before the delay elapses', t.visible(), true);

    t.advance(2);
    check('hidden once the delay elapses after a pause', t.visible(), false);

    t.play();
    t.fire('playing');
    check('reappears when the theme resumes', t.visible(), true);

    t.advance(HIDE_DELAY * 3);
    check('and stays up again', t.visible(), true);

    t.pause();
    t.fire('pause');
    t.play();
    t.fire('playing');
    t.advance(HIDE_DELAY * 2);
    check('a brief pause does not hide it once playback resumes', t.visible(), true);

    t.end();
    t.fire('ended');
    check('hidden immediately when the theme ends', t.visible(), false);

    // --- the labelling that says this is not a per-series control -----------

    const label = t.win.document.querySelector('.jtd-label');
    check('label says the control covers every theme', label && label.textContent, 'All themes');

    check('panel carries the scope note for hover and screen readers',
        t.panel().title, 'Theme music volume for every title - your account only.');

    const style = t.win.document.querySelector('style').textContent;
    const narrow = style.slice(style.indexOf('@media (max-width:600px)'));
    check('narrow screens drop the percentage', narrow.includes('.jtd-value{display:none;}'), true);
    check('narrow screens keep the label', narrow.includes('.jtd-label{display:none;}'), false);

    const failed = results.filter((r) => !r.ok);
    console.log(`\n${results.length - failed.length}/${results.length} passed`);
    process.exit(failed.length ? 1 : 0);
})();
