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
async function browser() {
  const errors = [];
  const console = new VirtualConsole(); console.on('jsdomError', (error) => errors.push(error.message));
  const dom = new JSDOM(readFileSync(resolve(web, 'index.html'), 'utf8'), { url: base + '/', runScripts: 'outside-only', pretendToBeVisual: true, virtualConsole: console });
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
  await until(() => window.document.querySelector('#content .movie-card') || window.document.querySelector('#login-error').textContent,
    'UI did not finish startup. ' + errors.join(' | '));
  assert.equal(window.document.querySelector('#login-error').textContent, '', errors.join(' | '));
  return { dom, window, requests, get: (selector) => window.document.querySelector(selector), core: modules.get(resolve(web, 'core.js')).namespace,
    ui: modules.get(resolve(web, 'ui.js')).namespace.ui, intercept: (callback) => { intercept = callback; }, close: () => window.close() };
}

test('grouped navigation, release footer, and offline changelog use the running version', async () => {
  const page = await browser();
  try {
    assert.equal(page.get('#app-version').textContent, 'v0.5.0');
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
