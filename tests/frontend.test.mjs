import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { mkdir, rm } from 'node:fs/promises';
import { resolve, dirname, basename, sep } from 'node:path';
import { spawn } from 'node:child_process';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import { JSDOM, VirtualConsole } from 'jsdom';

const project = process.cwd();
const web = resolve(project, 'src/Elementarr/wwwroot');
const data = resolve(project, '.local/frontend-tests-' + crypto.randomUUID());
let server, base, output = '';

/** Waits for an observable DOM/server condition with a bounded failure diagnostic. */
async function until(condition, message, timeout = 8000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await condition()) return; await new Promise((done) => setTimeout(done, 20)); }
  throw new Error(message);
}

before(async () => {
  await mkdir(data, { recursive: true });
  server = spawn('dotnet', ['tests/Elementarr.Tests/bin/Release/net10.0/Elementarr.Tests.dll', '--preview', '--preview-port', '0', '--preview-data', data],
    { cwd: project, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const collect = (chunk) => { output = (output + chunk).slice(-20000); const match = output.match(/PREVIEW_URL=(http:\/\/127\.0\.0\.1:\d+)/); if (match) base = match[1]; };
  server.stdout.on('data', collect); server.stderr.on('data', collect);
  await until(() => base || server.exitCode !== null, 'Fixture server did not start. ' + output, 30000);
  assert.ok(base, output);
});

after(async () => {
  if (server?.exitCode === null) { const stopped = new Promise((done) => server.once('exit', done)); server.kill(); await stopped; }
  const root = resolve(project, '.local') + sep;
  assert.ok(data.startsWith(root) && basename(data).startsWith('frontend-tests-'), 'Unsafe test cleanup path');
  await rm(data, { recursive: true, force: true });
});

/** Runs production UI modules in a DOM with real fixture HTTP endpoints; only native drawer/dialog primitives are substituted. */
async function browser(query = '') {
  const errors = [];
  const console = new VirtualConsole(); console.on('jsdomError', (error) => errors.push(error.message));
  const dom = new JSDOM(readFileSync(resolve(web, 'index.html'), 'utf8'), { url: base + '/' + query, runScripts: 'outside-only', pretendToBeVisual: true, virtualConsole: console });
  const window = dom.window, context = dom.getInternalVMContext();
  const requests = [];
  let intercept;
  context.AbortController = AbortController;
  context.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
  const select = window.document.querySelectorAll.bind(window.document);
  window.document.querySelectorAll = (selector) => select(selector === 'dialog:modal' ? 'dialog[open]' : selector);
  window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event('close')); };
  window.HTMLElement.prototype.scrollIntoView = function () {};
  // The DOM model has no browser top layer; test notification behavior with its container directly focusable.
  const notifications = window.document.querySelector('#notifications');
  notifications.removeAttribute('popover'); notifications.showPopover = undefined;
  /** Fetches only loopback fixture resources and maintains the browser's real authentication/CSRF cookie jar. */
  const fetchLocal = async (path, options = {}) => {
    const url = new URL(path, base); assert.equal(url.origin, base, 'UI attempted an external request');
    const headers = new Headers(options.headers);
    const cookies = await dom.cookieJar.getCookieString(url.href); if (cookies) headers.set('Cookie', cookies);
    const response = await fetch(url, { ...options, headers, redirect: 'manual' });
    for (const cookie of response.headers.getSetCookie()) await dom.cookieJar.setCookie(cookie, url.href);
    return response;
  };
  await fetchLocal('/__fixture/sign-in');
  context.fetch = async (path, options = {}) => {
    requests.push({ url: new URL(path, base), options });
    const pending = intercept?.(new URL(path, base), options, fetchLocal);
    return pending || fetchLocal(path, options);
  };
  const modules = new Map();
  /** Links the existing module graph while replacing the already-tested Bootstrap drawer implementation with DOM-only primitives. */
  const load = (file) => {
    if (modules.has(file)) return modules.get(file);
    let module;
    if (file.endsWith('bootstrap.bundle.min.js')) {
      class Drawer {
        constructor(element) { this.element = element; }
        async show() { this.element.open = true; this.element.dispatchEvent(new window.Event('show.bs.drawer')); }
        async hide() { this.element.open = false; this.element.dispatchEvent(new window.Event('hidden.bs.drawer')); }
        async toggle() { return this.element.open ? this.hide() : this.show(); }
      }
      module = new SyntheticModule(['Drawer'], function () { this.setExport('Drawer', Drawer); }, { context, identifier: file });
    } else module = new SourceTextModule(readFileSync(file, 'utf8'), { context, identifier: file });
    modules.set(file, module); return module;
  };
  const app = load(resolve(web, 'app.js'));
  await app.link((name, parent) => load(resolve(dirname(parent.identifier), name)));
  await app.evaluate();
  await until(() => window.document.querySelector('#content .movie-card') || (query && !window.document.querySelector('#admin').hidden) || window.document.querySelector('#login-error').textContent,
    'UI did not finish startup. ' + errors.join(' | '));
  assert.equal(window.document.querySelector('#login-error').textContent, '', errors.join(' | '));
  return { dom, window, requests, get: (selector) => window.document.querySelector(selector), core: modules.get(resolve(web, 'core.js')).namespace,
    ui: modules.get(resolve(web, 'ui.js')).namespace.ui, intercept: (callback) => { intercept = callback; }, close: () => window.close() };
}

test('grouped navigation, release footer, and offline changelog use the running version', async () => {
  const page = await browser();
  try {
    assert.equal(page.get('#app-version').textContent, 'v0.5.2');
    assert.equal(page.get('#account-name').tagName, 'STRONG');
    assert.deepEqual([...page.window.document.querySelectorAll('.nav-group > summary > span')].map((x) => x.textContent),
      ['Explore Movies', 'Explore TV', 'Your Movies', 'Your Television', 'Manage']);
    page.get('[data-view="changelog"]').click();
    await until(() => page.get('#changelog .release-note'), 'Changelog did not render');
    assert.match(page.get('#changelog').textContent, /0\.5\.0/);
    assert.equal(page.get('#search').disabled, true);
  } finally { page.close(); }
});

test('typing searches by AJAX without submission and keeps focus on the input', async () => {
  const page = await browser();
  try {
    page.get('[data-view="library"]').click(); await until(() => page.get('#content').textContent.includes('Watched Movie'), 'Collection did not load');
    const start = page.requests.length, input = page.get('#search'); input.focus();
    for (const text of ['I', 'Inc', 'Incomplete']) { input.value = text; input.dispatchEvent(new page.window.Event('input', { bubbles: true })); }
    await until(() => page.get('#content').textContent.includes('Incomplete Movie') && !page.get('#content').textContent.includes('Watched Movie'), 'Live search did not filter the collection');
    const queries = page.requests.slice(start).filter((request) => request.url.pathname === '/api/browse');
    assert.equal(queries.length, 1); assert.equal(queries[0].url.searchParams.get('query'), 'Incomplete');
    assert.equal(page.window.document.activeElement, input);
  } finally { page.close(); }
});

test('late search responses cannot replace newer results, even if an upstream ignores cancellation', async () => {
  const page = await browser(); let release;
  try {
    page.get('[data-view="library"]').click(); await until(() => page.get('#content').textContent.includes('Watched Movie'), 'Collection did not load');
    page.intercept((url, options, original) => {
      if (url.pathname === '/api/browse' && url.searchParams.get('query') === 'Watched')
        return new Promise((done) => { release = async () => done(await original(url, { ...options, signal: undefined })); });
    });
    const input = page.get('#search'); input.value = 'Watched'; input.dispatchEvent(new page.window.Event('input'));
    await until(() => release, 'Older request did not start');
    input.value = 'Incomplete'; input.dispatchEvent(new page.window.Event('input'));
    await until(() => page.get('#content').textContent.includes('Incomplete Movie') && !page.get('#content').textContent.includes('Watched Movie'), 'New search did not finish');
    await release(); await new Promise((done) => setTimeout(done, 100));
    assert.ok(!page.get('#content').textContent.includes('Watched Movie'));
    assert.equal(page.requests.find((request) => request.url.searchParams.get('query') === 'Watched').options.signal.aborted, true);
  } finally { page.close(); }
});

test('logout first asks for confirmation; cancel keeps the session and confirm signs out', async () => {
  const page = await browser();
  try {
    page.get('#logout').click(); assert.equal(page.get('#logout-dialog').open, true);
    assert.equal(page.requests.some((request) => request.url.pathname === '/api/logout'), false);
    page.get('#cancel-logout').click(); assert.equal(page.get('#logout-dialog').open, false); assert.equal(page.get('#app-view').hidden, false);
    page.get('#logout').click(); page.get('#confirm-logout').click();
    await until(() => page.get('#app-view').hidden, 'Logout did not complete');
    assert.equal(page.get('#login-view').hidden, false);
  } finally { page.close(); }
});

test('toast countdown pauses for keyboard focus, escapes text, then fades and dismisses automatically', async () => {
  const page = await browser();
  try {
    assert.equal(page.ui.settings.toastDuration, 5); page.ui.settings.toastDuration = 1;
    const notice = page.core.notify('<img src=x onerror=alert(1)>', true);
    assert.equal(notice.querySelector('img'), null); assert.ok(notice.textContent.includes('<img'));
    await new Promise((done) => setTimeout(done, 250));
    const fraction = Number(notice.querySelector('.notification-progress').style.transform.match(/[\d.]+/)[0]);
    assert.ok(fraction > 0 && fraction < 1);
    notice.querySelector('button').focus(); await new Promise((done) => setTimeout(done, 1100)); assert.equal(notice.isConnected, true);
    page.get('#search').focus(); await until(() => !notice.isConnected, 'Toast did not resume and automatically dismiss', 2500);
  } finally { page.close(); }
});

test('new TV navigation renders real upcoming episode information and keeps affected history separate', async () => {
  const page = await browser();
  try {
    page.get('[data-view="tv-upcoming"]').click();
    await until(() => page.get('#content').textContent.includes('Next Episode'), 'Episode calendar did not render');
    assert.ok(page.get('#content').textContent.includes('S02E09'));
    page.get('[data-view="tv-issues"]').click();
    await until(() => page.get('#page-title').textContent.includes('episode') && page.get('#content .empty-state') && !page.get('#content').hasAttribute('aria-busy'), 'Affected TV view did not finish');
    assert.equal(page.get('#content .movie-card'), null);
  } finally { page.close(); }
});

test('appearance controls expose distinct typography groups and preview duration and badge corners in their actual units', async () => {
  const page = await browser();
  try {
    page.get('[data-view="admin"]').click(); await until(() => page.get('[name="toastDuration"]'), 'Appearance controls did not render');
    page.get('[name="fontSize"]').value = '22'; page.get('[name="navigationFontScale"]').value = '125';
    page.get('[name="badgeRadius"]').value = '0'; page.get('[name="toastDuration"]').value = '2';
    assert.match(page.get('[name="navigationFontScale"]').nextElementSibling.textContent, /%/);
    assert.match(page.get('[name="toastDuration"]').nextElementSibling.textContent, /s/);
    page.get('#preview-appearance').click();
    assert.equal(page.ui.settings.fontSize, 22); assert.equal(page.ui.settings.navigationFontScale, 125);
    assert.equal(page.ui.settings.badgeRadius, 0); assert.equal(page.ui.settings.toastDuration, 2);
    assert.equal(page.window.document.documentElement.style.getPropertyValue('--base-size'), '22px');
    assert.equal(page.window.document.documentElement.style.getPropertyValue('--badge-radius'), '0px');
  } finally { page.close(); }
});

test('Connections has five equal cards, both media totals, and history sync requests both media types', async () => {
  const page = await browser();
  try {
    page.get('[data-view="settings"]').click();
    await until(() => !page.get('#settings').hidden && page.get('#stat-tv-watched').textContent === '1', 'Connections did not finish');
    assert.equal(page.get('#settings #server-form'), null); assert.equal(page.get('#settings #updates-form'), null);
    assert.equal(page.window.document.querySelectorAll('#connections-form .connection-grid > .connection-card').length, 5);
    assert.equal(page.get('#connections-form .wide'), null);
    assert.match(page.get('#stats').textContent, /movies in collection.*movies watched.*TV shows in collection.*episodes watched/);
    const sync = page.get('#connections-form [data-sync="history"]'); assert.equal(sync.textContent, 'Sync History');
    sync.click(); await until(() => page.requests.some((x) => x.url.pathname === '/api/sync' && x.url.searchParams.get('source') === 'history'), 'Combined history sync was not requested');
  } finally { page.close(); }
});

test('Admin update and server tabs select separate forms and the tray link opens Admin updates', async () => {
  const page = await browser('?settings=updates');
  try {
    await until(() => page.get('[data-admin-tab="updates"]').classList.contains('active'), 'Tray link did not select Updates');
    assert.equal(page.get('#admin').hidden, false); assert.equal(page.get('#admin-updates').hidden, false);
    assert.equal(page.get('#appearance-form').hidden, true); assert.equal(page.get('#admin-server').hidden, true);
    assert.equal(page.get('#updates-form').parentElement.id, 'admin-updates');
    page.get('[data-admin-tab="server"]').click();
    assert.equal(page.get('#admin-server').hidden, false); assert.equal(page.get('#admin-updates').hidden, true);
    assert.equal(page.get('#server-form').parentElement.id, 'admin-server');
    assert.equal(page.get('#server-form').closest('form').id, 'server-form');
    page.get('[data-admin-tab="appearance"]').click(); assert.equal(page.get('#appearance-form').hidden, false);
  } finally { page.close(); }
});

test('TV episode timeline and drawer share the movie composition with episode artwork, facts, ratings and links', async () => {
  const page = await browser();
  try {
    page.get('[data-view="tv-history"]').click();
    await until(() => page.get('#content .timeline-rating-strip'), 'Rich TV timeline did not render');
    assert.match(page.get('.timeline-thumbnail').src, /episode-still.jpg$/);
    assert.match(page.get('.timeline-poster').src, /poster.jpg$/);
    assert.match(page.get('.timeline-copy').textContent, /S02 E03/);
    assert.match(page.get('.timeline-copy .movie-meta').textContent, /24 min/);
    assert.match(page.get('.timeline-copy .movie-genres').textContent, /Comedy/);
    assert.equal(page.get('.timeline-rating-strip .tmdb strong').textContent, '8.7');
    assert.equal(page.get('.timeline-remove'), null); assert.equal(page.get('.timeline-match'), null);
    assert.equal(page.get('#timeline-thumbs').closest('label').hidden, false);
    page.get('#timeline-thumbs').click(); assert.ok(page.get('#timeline-rail').classList.contains('without-thumbnails'));
    page.get('#timeline-ratings').click(); assert.ok(page.get('#timeline-rail').classList.contains('without-ratings'));
    page.get('.timeline-open').click();
    await until(() => page.get('#tv-details .dialog-overview')?.textContent.includes('episode-specific') && !page.get('#tv-details .detail-loading'), 'Episode drawer did not finish');
    for (const selector of ['.movie-hero', '.movie-heading', '.genre-tags', '.detail-ratings', '.movie-facts', '.cast-grid', '.movie-links']) assert.ok(page.get('#tv-details ' + selector), selector);
    assert.match(page.get('#tv-details .movie-backdrop').src, /episode-still.jpg$/);
    assert.match(page.get('#tv-details .detail-poster').src, /poster.jpg$/);
    assert.ok(page.get('#tv-details a[href="https://www.themoviedb.org/tv/1/season/2/episode/3"]'));
    assert.ok(page.get('#tv-details a[href="https://www.imdb.com/title/tt7654321/"]'));
    assert.match(page.get('#tv-details .cast-grid').textContent, /Episode Actor/);
    assert.equal(page.get('#tv-details a[href*="/movie/"]'), null);
  } finally { page.close(); }
});

test('show flyouts use the movie design and never label a partly watched series as completed', async () => {
  const page = await browser();
  try {
    page.get('[data-view="tv-library"]').click(); await until(() => page.get('#content .movie-title-button')?.textContent.includes('Series 1'), 'TV collection did not load');
    page.get('#content .poster-button').click();
    await until(() => page.get('#tv-details .movie-logo') && !page.get('#tv-details .detail-loading'), 'Show details did not load');
    assert.match(page.get('#tv-details .movie-backdrop').src, /show-backdrop.jpg$/);
    assert.match(page.get('#tv-details .movie-facts').textContent, /Series Creator.*English|Series Creator.*en/);
    assert.equal(page.get('#tv-details .detail-ratings .tmdb strong').textContent, '8.1');
    assert.match(page.get('#tv-details .poster-state-icons').innerHTML, /Has watched episodes/);
    assert.ok(page.get('#tv-details a[href="https://www.themoviedb.org/tv/1"]'));
    assert.equal(page.get('#tv-drawer .movie-actions #tv-add').disabled, true);
    assert.equal(page.get('#movie-drawer').open, false);
  } finally { page.close(); }
});

test('movie history keeps its original ratings, drawer and removal action after shared TV rendering', async () => {
  const page = await browser();
  try {
    page.get('[data-view="history"]').click(); await until(() => page.get('#content .timeline-remove'), 'Movie timeline did not render');
    assert.equal(page.get('.timeline-remove').textContent, 'Remove movie from history');
    assert.ok(page.get('.timeline-rating-strip'));
    page.get('.timeline-open').click(); await until(() => page.get('#movie-drawer').open, 'Movie drawer did not open');
    assert.ok(page.get('#movie-details .movie-heading')); assert.equal(page.get('#tv-drawer').open, false);
  } finally { page.close(); }
});


test('TV filters use TV terminology, reach the backend, and stay independent of movie filters', async () => {
  const page = await browser();
  try {
    page.get('[data-view="tv-trending"]').click();
    await until(() => page.core.app.view === 'tv-trending' && page.get('#content .movie-card'), 'TV discovery did not load');
    assert.equal(page.get('#open-filters').hidden, false);
    assert.match(page.get('#filter-fields').textContent, /Managed by Sonarr/);
    assert.match(page.get('#filter-fields').textContent, /Episode runtime/);
    assert.equal(page.get('#filter-fields input[value="Action & Adventure"]').type, 'checkbox');
    page.get('#open-filters').click();
    page.get('#filter-form').elements.runtimeMin.value = '20';
    page.get('#filter-form').elements.runtimeMax.value = '25';
    page.get('#filter-form').elements.genres.forEach(input => { input.checked = input.value === 'Comedy'; });
    page.get('#filter-form').dispatchEvent(new page.window.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => page.requests.some(x => x.url.pathname === '/api/tv/browse' && x.url.searchParams.get('runtimeMin') === '20' && x.url.searchParams.get('genres') === 'Comedy'), 'TV filters were not passed to the API');
    page.get('[data-view="trending"]').click();
    await until(() => page.core.app.view === 'trending' && !page.get('#filter-fields').textContent.includes('Managed by Sonarr'), 'Movie filters did not return');
    assert.equal(page.get('#filter-form').elements.runtimeMin.value, '0');
    page.get('[data-view="tv-trending"]').click();
    await until(() => page.get('#filter-form').elements.runtimeMin.value === '20', 'TV filter state was lost');
    page.get('#filter-drawer').dispatchEvent(new page.window.Event('hidden.bs.drawer'));
    assert.equal(page.window.document.body.classList.contains('nav-collapsed'), false);
  } finally { page.close(); }
});

test('show season expansion fetches episodes once and displays exact watched flags and poster progress', async () => {
  const page = await browser();
  try {
    page.get('[data-view="tv-library"]').click();
    await until(() => page.get('#content .show-progress progress'), 'TV poster progress is missing');
    page.get('#content .poster-button').click();
    await until(() => page.get('#tv-details .tv-season') && !page.get('#tv-details .detail-loading'), 'Show seasons are missing');
    const seasons = [...page.window.document.querySelectorAll('#tv-details .tv-season')];
    const season = seasons.find(x => x.querySelector('summary').textContent.includes('Season 2'));
    season.open = true;
    await until(() => season.querySelector('.tv-episodes'), 'Season episodes did not load');
    assert.equal(season.querySelectorAll('.tv-episode').length, 4);
    assert.equal(season.querySelectorAll('.watched').length, 1);
    assert.match(season.querySelector('.watched').parentElement.textContent, /S02 E03/);
    const count = page.requests.filter(x => x.url.pathname === '/api/tv/1/season/2').length;
    season.open = false; season.dispatchEvent(new page.window.Event('toggle'));
    season.open = true; season.dispatchEvent(new page.window.Event('toggle'));
    assert.equal(page.requests.filter(x => x.url.pathname === '/api/tv/1/season/2').length, count);
  } finally { page.close(); }
});

test('Connections automatically checks saved services without submitting credentials and shows feedback', async () => {
  const page = await browser();
  try {
    page.get('[data-view="settings"]').click();
    await until(() => page.get('#radarr-status').textContent === 'Connected', 'Connections were not automatically checked');
    assert.ok(page.requests.some(x => x.url.pathname === '/api/connections/check'));
    assert.equal(page.requests.filter(x => x.url.pathname === '/api/settings' && x.options.method === 'POST').length, 0);
    assert.match(page.get('#notifications').textContent, /Re-checking saved connections/);
    assert.equal(page.get('#sonarr-status').closest('section').querySelector('[data-sync="tv-history"]'), null);
    assert.ok(page.get('#radarr-status').closest('section').querySelector('#refresh-movie-metadata'));
  } finally { page.close(); }
});

test('ordinary timeline wheel events retain page scrolling while Shift-wheel can navigate horizontally', async () => {
  const page = await browser();
  try {
    page.get('[data-view="history"]').click();
    await until(() => page.get('#timeline-rail'), 'Timeline did not load');
    const rail = page.get('#timeline-rail');
    Object.defineProperties(rail, { scrollWidth: { value: 1500 }, clientWidth: { value: 500 } });
    const vertical = new page.window.WheelEvent('wheel', { deltaY: 120, bubbles: true, cancelable: true });
    rail.dispatchEvent(vertical); assert.equal(vertical.defaultPrevented, false); assert.equal(rail.scrollLeft, 0);
    const horizontal = new page.window.WheelEvent('wheel', { deltaY: 120, shiftKey: true, bubbles: true, cancelable: true });
    rail.dispatchEvent(horizontal); assert.equal(horizontal.defaultPrevented, true); assert.equal(rail.scrollLeft, 120);
    rail.scrollLeft = 1000;
    const boundary = new page.window.WheelEvent('wheel', { deltaY: 120, shiftKey: true, bubbles: true, cancelable: true });
    rail.dispatchEvent(boundary); assert.equal(boundary.defaultPrevented, false);
  } finally { page.close(); }
});
