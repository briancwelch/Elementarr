import { ui, t } from './ui.js';

/** Finds a single UI element. */
export const $ = (selector) => document.querySelector(selector);
export const app = { csrf: '', view: 'for-you', page: 1, query: '', settings: null, selected: null,
  sequence: 0, detailSequence: 0, plexFlow: 0, filters: {}, preferences: {}, pageData: null, match: null };

/** Escapes movie metadata and configurable wording before placing them in a generated HTML fragment. */
export function escape(value) {
  return String(value ?? '').replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

/** Calls the local authenticated API with CSRF on writes and returns only sanitized server errors. */
export async function api(path, body, options = {}) {
  const response = await fetch(`/api/${path}`, { method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', signal: options.signal,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': app.csrf },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    if (response.status === 401 && path !== 'login') document.dispatchEvent(new Event('elementarr:session-expired'));
    throw new Error(data.error || (response.status === 429 ? t('error.rateLimit')
      : response.status === 401 ? t(path === 'login' ? 'error.password' : 'error.signIn') : t('error.request', { status: response.status })));
  }
  return data;
}

/** Shows themed notifications with a configurable countdown, pause-on-hover/focus, and an automatic fade. */
export function notify(message, error = false) {
  const notice = document.createElement('div'); notice.className = `notification${error ? ' error' : ''}`;
  notice.setAttribute('role', error ? 'alert' : 'status');
  notice.innerHTML = `<i class="fa-solid ${error ? 'fa-circle-exclamation' : 'fa-circle-check'}" aria-hidden="true"></i><span>${escape(message)}</span><button class="btn-text btn-icon" aria-label="${escape(t('action.dismiss'))}"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button><span class="notification-progress" aria-hidden="true"></span>`;
  const duration = Math.min(60, Math.max(1, Number(ui.settings.toastDuration) || 5)) * 1000;
  let remaining = duration, last = performance.now(), paused = false, closing = false;
  const progress = notice.querySelector('.notification-progress');
  // Keep countdown motion functional independently of decorative animation preferences.
  const close = () => {
    if (closing) return; closing = true; clearInterval(timer); notice.classList.add('leaving');
    setTimeout(() => notice.remove(), ui.settings.animations && !matchMedia('(prefers-reduced-motion: reduce)').matches ? 180 : 0);
  };
  const advance = () => {
    const now = performance.now(); if (!paused) remaining -= now - last; last = now;
    progress.style.transform = `scaleX(${Math.max(0, remaining) / duration})`;
    if (remaining <= 0) close();
  };
  const updatePause = () => { advance(); paused = notice.matches(':hover') || notice.contains(document.activeElement); };
  const timer = setInterval(() => { if (notice.isConnected) advance(); else clearInterval(timer); }, 50);
  for (const event of ['mouseenter', 'mouseleave', 'focusin']) notice.addEventListener(event, updatePause);
  notice.addEventListener('focusout', () => queueMicrotask(updatePause));
  notice.querySelector('button').addEventListener('click', close);
  $('#notifications').append(notice); placeNotifications();
  return notice;
}

/** Keeps notifications above native modal drawers and interactive until dismissed. */
export function placeNotifications() {
  const host = $('#notifications');
  const modal = [...document.querySelectorAll('dialog:modal')].at(-1);
  const parent = modal || document.body;
  if (host.parentElement !== parent) parent.append(host);
  if (host.children.length && typeof host.showPopover === 'function' && !host.matches(':popover-open')) host.showPopover();
}

/** Disables an action until its request finishes and reports accessible busy state. */
export async function busy(button, work) {
  if (button.disabled) return;
  button.disabled = true; button.setAttribute('aria-busy', 'true');
  try { return await work(); } finally { button.disabled = false; button.removeAttribute('aria-busy'); }
}

/** Formats optional runtime and rating values without inventing scores. */
export function number(value, digits = 1) {
  return value == null || !Number.isFinite(Number(value)) ? t('status.na') : Number(value).toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: digits });
}

/** Builds only validated TMDB image URLs. */
export function imageUrl(path, size = 'w500') {
  return /^\/[a-zA-Z0-9_-]+\.(jpg|png|webp)$/.test(path || '') ? `https://image.tmdb.org/t/p/${size}${path}` : null;
}
