import { Drawer } from './vendor/bootstrap/bootstrap.bundle.min.js';
import { $, app, api, busy, escape, imageUrl, notify, placeNotifications } from './core.js';
import { ui, t, registerTexts, applyTexts, loadAppearance, readPreferences, savePreferences } from './ui.js';
import { buildFilters, readFilters, writeFilters, updateFilterSummary, buildSortOptions } from './filters.js';
import { renderMovies, renderHistory, renderDetails, applyTimelinePreferences, protectImages } from './movies.js';
import { bindAdmin, renderAdmin, adminTab } from './admin.js';
import { bindUpdates, loadUpdates } from './updates.js';
import { tvViews, bindTv, loadTvPage, renderTvSeries, renderTvHistory } from './tv.js';

Object.assign(ui.words, { 'connections.reconnectPlex': 'Reconnect Plex', 'status.searchResults': 'Search results for “{query}”',
  'status.savedConnection': 'Saved',
  'status.detailLoading': 'Loading the full movie story…', 'status.sourceNotices': 'Review source notices',
  'view.history.caption': 'Watched Movie History',
  'status.localFilterHelp': 'Some movies have incomplete indexed metadata. Refresh movie metadata in Administration to fill missing fields.' });

const drawers = { filters: new Drawer($('#filter-drawer'), { scroll: true, backdrop: false }),
  movie: new Drawer($('#movie-drawer')), match: new Drawer($('#match-drawer')) };
const browseViews = ['for-you', 'trending', 'upcoming', 'top-rated', 'library', 'history', 'issues', ...tvViews];
const viewFilters = {};
const tvGenres = ['Action & Adventure', 'Animation', 'Comedy', 'Crime', 'Documentary', 'Drama', 'Family', 'Kids', 'Mystery', 'News', 'Reality', 'Sci-Fi & Fantasy', 'Soap', 'Talk', 'War & Politics', 'Western'].map((name) => ({ name }));
let contentRequest;
let searchTimer;

/** Refreshes the browser-bound CSRF token and switches between authenticated and Plex-only sign-in views. */
async function session() {
  const result = await api('session'); app.csrf = result.csrfToken;
  $('#app-version').textContent = 'v' + result.version;
  $('#plex-login').disabled = !result.plexSignInAvailable; $('#plex-login-help').hidden = !result.firstRun;
  $('#login-view').hidden = result.authenticated; $('#app-view').hidden = !result.authenticated;
  if (!result.authenticated) { Object.values(drawers).forEach((drawer) => { void drawer.hide(); }); $('#plex-login').focus(); }
  return result.authenticated;
}

/** Loads safe connection values, retaining explicitly saved Radarr destinations. */
async function loadSettings() {
  app.settings = await api('settings'); const settings = app.settings;
  await loadUpdates();
  if (!$('#account-avatar').getAttribute('src')) $('#account-avatar').src = '/api/plex/avatar';
  const server = await api('server');
  $('#server-port').value = server.settings.port; $('#server-host').value = server.settings.hostName || 'localhost'; $('#server-bind').value = server.settings.bindAddress;
  for (const id of ['server-port', 'server-host', 'server-bind', 'server-save']) $('#' + id).disabled = server.container;
  $('#server-help').textContent = server.container ? 'Docker: change ELEMENTARR_PORT in your Compose .env file, then recreate the container.' : server.restartRequired ? 'Saved. Restart Elementarr to apply the new port or network access.' : 'Changes apply after restarting Elementarr. Local network access may require a Windows Firewall rule.';
  for (const [id, key] of [['radarr-url', 'radarrUrl'], ['sonarr-url', 'sonarrUrl'], ['tautulli-url', 'tautulliUrl'], ['plex-url', 'plexUrl'], ['region', 'region']]) $('#' + id).value = settings[key];
  $('#tautulli-enabled').checked = settings.tautulliEnabled; $('#tautulli-fields').hidden = !settings.tautulliEnabled;
  $('#plex-account').textContent = t(settings.hasPlexToken ? 'status.plexLinked' : 'status.plexUnlinked', { name: settings.plexOwnerName });
  $('#connect-plex span').textContent = t(settings.hasPlexToken ? 'connections.reconnectPlex' : 'connections.connectPlex');
  for (const [source, flag] of [['radarr', 'hasRadarrKey'], ['sonarr', 'hasSonarrKey'], ['tautulli', 'hasTautulliKey'], ['tmdb', 'hasTmdbToken'], ['plex', 'hasPlexToken']]) {
    const configured = settings[flag] && (source !== 'tautulli' || settings.tautulliEnabled);
    $('#' + source + '-status').textContent = t(configured ? 'status.savedConnection' : source === 'tautulli' ? 'status.disabled' : 'status.notConfigured');
  }
  for (const [id, key, label] of [['radarr-key', 'hasRadarrKey', 'connections.apiKey'], ['sonarr-key', 'hasSonarrKey', 'connections.apiKey'], ['tautulli-key', 'hasTautulliKey', 'connections.apiKey'], ['tmdb-token', 'hasTmdbToken', 'connections.tmdbToken']]) $('#' + id).placeholder = t(settings[key] ? 'status.savedSecret' : label);
  fillOptions('#plex-user', settings.plexUserId ? [{ id: settings.plexUserId, name: settings.plexUsername }] : [], settings.plexUserId, t('status.resolveUser', { name: settings.plexUsername }));
  fillOptions('#plex-server', settings.plexServerId ? [{ id: settings.plexServerId, name: settings.plexServerId }] : [], settings.plexServerId, 'Load your Plex servers');
  fillOptions('#sonarr-profile', settings.sonarrQualityProfileId ? [{ id: String(settings.sonarrQualityProfileId), name: 'Saved profile #' + settings.sonarrQualityProfileId }] : [], String(settings.sonarrQualityProfileId), t('status.loadProfiles'));
  fillOptions('#sonarr-root', settings.sonarrRootFolder ? [{ id: settings.sonarrRootFolder, name: settings.sonarrRootFolder }] : [], settings.sonarrRootFolder, t('status.loadFolders'));
  fillOptions('#quality-profile', settings.qualityProfileId ? [{ id: String(settings.qualityProfileId), name: t('status.savedProfile', { id: settings.qualityProfileId }) }] : [], String(settings.qualityProfileId), t('status.loadProfiles'));
  fillOptions('#root-folder', settings.rootFolder ? [{ id: settings.rootFolder, name: settings.rootFolder }] : [], settings.rootFolder, t('status.loadFolders'));
}

/** Populates an option list without selecting an arbitrary destination or profile. */
function fillOptions(selector, options, selected, placeholder) {
  const select = $(selector); select.replaceChildren(new Option(placeholder, ''));
  options.forEach((option) => select.add(new Option(option.name, option.id)));
  select.value = options.some((option) => option.id === selected) ? selected : '';
}

/** Saves credentials on the server while blank password fields retain saved secrets. */
async function saveConnections() {
  if (!$('#connections-form').reportValidity()) throw new Error(t('error.form'));
  const user = $('#plex-user');
  await api('settings', { sonarrUrl: $('#sonarr-url').value, sonarrKey: $('#sonarr-key').value, sonarrQualityProfileId: Number($('#sonarr-profile').value) || 0, sonarrRootFolder: $('#sonarr-root').value, radarrUrl: $('#radarr-url').value, radarrKey: $('#radarr-key').value,
    tautulliUrl: $('#tautulli-url').value, tautulliKey: $('#tautulli-key').value, tmdbToken: $('#tmdb-token').value,
    region: $('#region').value, plexUserId: user.value, plexUsername: user.value ? user.selectedOptions[0].text : app.settings.plexUsername,
    qualityProfileId: Number($('#quality-profile').value) || 0, rootFolder: $('#root-folder').value,
    plexUrl: $('#plex-url').value, tautulliEnabled: $('#tautulli-enabled').checked });
  ['radarr-key', 'sonarr-key', 'tautulli-key', 'tmdb-token'].forEach((id) => { $('#' + id).value = ''; });
  app.settings = await api('settings');
  ['radarr-key', 'sonarr-key', 'tautulli-key', 'tmdb-token'].forEach((id) => { $('#' + id).placeholder = t('status.savedSecret'); });
  await loadState();
}

/** Shows source freshness and actionable history notices without exposing service credentials. */
async function loadState() {
  const state = await api('state'); app.state = state;
  const tv = tvViews.includes(app.view);
  $('#stat-library').textContent = state.librarySyncedAt ? state.libraryCount.toLocaleString() : t('status.na');
  $('#stat-watched').textContent = state.historySyncedAt ? state.watchedCount.toLocaleString() : t('status.na');
  $('#stat-tv-library').textContent = state.tvLibrarySyncedAt ? state.tvLibraryCount.toLocaleString() : t('status.na');
  $('#stat-tv-watched').textContent = state.tvHistorySyncedAt ? state.tvWatchedCount.toLocaleString() : t('status.na');
  $('#nav-count').textContent = state.librarySyncedAt ? state.libraryCount.toLocaleString() : t('status.na');
  $('#nav-count').hidden = !state.librarySyncedAt;
  $('#tv-nav-count').textContent = state.tvLibraryCount.toLocaleString(); $('#tv-nav-count').hidden = !state.tvLibrarySyncedAt;
  $('#tv-issue-count').textContent = state.tvUnmatchedCount.toLocaleString(); $('#tv-issue-count').hidden = !state.tvUnmatchedCount;
  $('#issue-count').textContent = state.unmatchedCount.toLocaleString(); $('#issue-count').hidden = !state.unmatchedCount;
  $('#account-name').textContent = state.username; $('#server-address').textContent = location.host;
  const stamp = (date) => date ? new Date(date).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : t('status.notSynced');
  $('#sync-status').textContent = t('status.freshness', { collection: stamp(tv ? state.tvLibrarySyncedAt : state.librarySyncedAt), history: stamp(tv ? state.tvHistorySyncedAt : state.historySyncedAt) });
  $('#warnings').replaceChildren();
  if (state.unmatchedCount && !tv) {
    const warning = document.createElement('div'); warning.className = 'source-warning';
    warning.innerHTML = `<i class="fa-solid fa-link-slash" aria-hidden="true"></i><span>${escape(t('status.unmatchedWarning', { count: state.unmatchedCount }))}</span><button class="btn-text">${escape(t('action.viewAffected'))}<i class="fa-solid fa-arrow-right" aria-hidden="true"></i></button>`;
    warning.querySelector('button').addEventListener('click', () => navigate('issues')); $('#warnings').append(warning);
  }
  for (const warning of state.warnings.filter((text) => !text.includes('could not be matched to TMDB'))) {
    const element = document.createElement('p'); element.className = 'source-warning'; element.textContent = warning; $('#warnings').append(element);
  }
  $('#onboarding').hidden = !browseViews.includes(app.view) || Boolean((tv ? app.settings?.hasSonarrKey : app.settings?.hasRadarrKey) && app.settings?.hasTmdbToken);
  document.querySelectorAll('[data-sync="radarr"]').forEach((button) => { button.disabled = !app.settings?.hasRadarrKey; });
  document.querySelectorAll('[data-sync="history"]').forEach((button) => { button.disabled = !(app.settings?.hasPlexToken || (app.settings?.tautulliEnabled && app.settings?.hasTautulliKey)); });
  if (tv && state.tvUnmatchedCount) {
    const warning = document.createElement('div'); warning.className = 'source-warning';
    warning.innerHTML = `<i class="fa-solid fa-link-slash" aria-hidden="true"></i><span>${state.tvUnmatchedCount} TV episode watches need a series identifier.</span><button class="btn-text">View Affected TV History</button>`;
    warning.querySelector('button').addEventListener('click', () => navigate('tv-issues')); $('#warnings').append(warning);
  }
  document.querySelectorAll('[data-sync="sonarr"]').forEach((button) => { button.disabled = !app.settings?.hasSonarrKey; });
  document.querySelectorAll('[data-sync="tv-history"]').forEach((button) => { button.disabled = !app.settings?.hasPlexToken || !app.settings?.plexServerId; });
  return state;
}

/** Restores display choices using administrator defaults where this browser has no override. */
function initPreferences() {
  const stored = readPreferences(); const settings = ui.settings;
  const bounded = (value, fallback, min, max) => Math.min(max, Math.max(min, Number(value) || fallback));
  app.preferences = { navGroups: stored.navGroups && typeof stored.navGroups === 'object' ? stored.navGroups : {}, collapsed: typeof stored.collapsed === 'boolean' ? stored.collapsed : window.innerWidth < 900, layout: stored.layout === 'list' ? 'list' : 'grid',
    pageSize: [10, 25, 50, 100].includes(Number(stored.pageSize)) ? Number(stored.pageSize) : settings.pageSize,
    posterSize: bounded(stored.posterSize, settings.posterSize, 120, 320), timelineZoom: bounded(stored.timelineZoom, settings.timelineZoom, 130, 360),
    timelinePosters: typeof stored.timelinePosters === 'boolean' ? stored.timelinePosters : settings.showPosters,
    timelineThumbnails: typeof stored.timelineThumbnails === 'boolean' ? stored.timelineThumbnails : settings.showBackdrops,
    timelineRatings: typeof stored.timelineRatings === 'boolean' ? stored.timelineRatings : settings.showRatings };
  applyPreferences();
}

/** Applies responsive navigation, grid sizing, timeline controls, and browser display defaults. */
function applyPreferences() {
  const preferences = app.preferences;
  document.body.classList.toggle('nav-collapsed', preferences.collapsed);
  document.querySelectorAll('[data-nav-group]').forEach((group) => { group.open = preferences.collapsed || preferences.navGroups[group.dataset.navGroup] !== false; group.querySelector('summary').hidden = preferences.collapsed; });
  document.documentElement.style.setProperty('--poster-size', preferences.posterSize + 'px');
  $('#collapse-nav').setAttribute('aria-expanded', String(!preferences.collapsed));
  $('#collapse-nav').setAttribute('aria-label', t(preferences.collapsed ? 'action.expand' : 'action.collapse'));
  $('#page-size').value = String(preferences.pageSize); $('#poster-size').value = preferences.posterSize;
  $('#timeline-zoom').value = preferences.timelineZoom; $('#timeline-posters').checked = preferences.timelinePosters;
  $('#timeline-thumbs').checked = preferences.timelineThumbnails; $('#timeline-ratings').checked = preferences.timelineRatings;
  $('#grid-mode').classList.toggle('active', preferences.layout === 'grid'); $('#list-mode').classList.toggle('active', preferences.layout === 'list');
  applyTimelinePreferences();
}

/** Changes main navigation while keeping each list's own filters, sorting, and independent AJAX pagination. */
async function navigate(view) {
  if (![...browseViews, 'settings', 'admin', 'changelog'].includes(view)) return;
  clearTimeout(searchTimer); contentRequest?.abort(); app.sequence++;
  if (app.navigated && browseViews.includes(app.view)) viewFilters[app.view] = { ...app.filters };
  app.navigated = true;
  app.view = view; app.page = 1; app.query = ''; $('#search').value = '';
  if (window.innerWidth < 760) { app.preferences.collapsed = true; applyPreferences(); }
  app.filters = viewFilters[view] || (view === 'tv-upcoming' ? { sort: 'default', order: 'asc' } : ['library', 'tv-library'].includes(view) ? { sort: 'title', order: 'asc' }
    : ['history', 'issues', 'tv-history', 'tv-issues'].includes(view) ? { sort: 'watched', order: 'desc' } : { sort: 'default', order: 'desc', owned: 'no', watched: 'no' });
  document.querySelectorAll('.nav-item').forEach((button) => {
    const active = button.dataset.view === view; button.classList.toggle('active', active);
    if (active) { button.setAttribute('aria-current', 'page'); const group = button.closest('details'); if (group) group.open = true; } else button.removeAttribute('aria-current');
  });
  updateHeading(); buildSortOptions(); if (app.filterOptions) buildFilters(view.startsWith('tv-') ? tvGenres : app.filterOptions.genres); writeFilters();
  const browse = browseViews.includes(view); const timeline = ['history', 'issues', 'tv-history', 'tv-issues'].includes(view);
  $('#settings').hidden = view !== 'settings'; $('#admin').hidden = view !== 'admin'; $('#changelog').hidden = view !== 'changelog';
  $('#content').hidden = !browse; $('#browse-toolbar').hidden = !browse; $('#open-filters').hidden = !browse;
  for (const id of ['sort-by', 'sort-order']) $('#' + id).closest('label').hidden = false;
  $('#page-size').disabled = false; $('#page-size').value = String(app.preferences.pageSize);
  $('#timeline-controls').hidden = !timeline; $('#search').disabled = !browse;
  $('.poster-size-control').hidden = timeline; $('.view-buttons').hidden = timeline; $('#active-filters').hidden = !browse;
  $('#pagination').hidden = true; $('#feed-notice').textContent = ''; $('#result-count').textContent = '';
  if (!browse) { app.sequence++; await drawers.filters.hide(); }
  await loadState();
  if (view === 'settings') void checkConnections();
  if (view === 'admin') renderAdmin(); else if (view === 'changelog') await loadChangelog(); else if (browse) await loadContent();
}

/** Debounces typing and immediately invalidates older results, including requests that complete before the next search starts. */
function queueSearch() {
  clearTimeout(searchTimer); contentRequest?.abort(); app.sequence++;
  searchTimer = setTimeout(() => { void searchCurrentView(); }, 300);
}

/** Searches the current view without a page reload or losing keyboard focus. */
async function searchCurrentView() {
  if (!browseViews.includes(app.view)) return;
  app.query = $('#search').value.trim(); app.page = 1; await loadContent({ live: true });
}

/** Displays bundled release notes offline without trusting upstream HTML or consuming GitHub API requests. */
async function loadChangelog() {
  try {
    const data = await api('changelog'); if (app.view !== 'changelog') return;
    $('#changelog').innerHTML = data.releases.map((release) => `<article class="release-note connection-card"><div class="release-heading"><h2>${escape(release.title)}</h2><span class="badge badge-subtle theme-primary">v${escape(release.version)}</span><time datetime="${escape(release.date)}">${escape(new Date(release.date + 'T12:00:00').toLocaleDateString())}</time></div><ul>${release.changes.map((change) => `<li>${escape(change)}</li>`).join('')}</ul></article>`).join('');
  } catch (error) { $('#changelog').textContent = error.message; }
}

/** Rebuilds configurable page headings without treating user wording as HTML. */
function updateHeading() {
  for (const [id, key] of [['page-eyebrow', 'eyebrow'], ['page-title', 'title'], ['page-description', 'description'], ['feed-title', 'caption']]) $('#' + id).textContent = t(`view.${app.view}.${key}`);
  document.title = `${t('view.' + app.view + '.caption')} · ${t('brand.name')}`;
}

/** Renders an honest loading, empty, or failure state with an optional retry button. */
function empty(title, description, retry = false) {
  $('#content').innerHTML = `<div class="empty-state"><i class="fa-solid fa-film" aria-hidden="true"></i><h2>${escape(title)}</h2><p>${escape(description)}</p>${retry ? `<button id="retry" class="btn-outline">${escape(t('action.retry'))}</button>` : ''}</div>`;
  $('#retry')?.addEventListener('click', loadContent);
}

/** Loads filtered and sorted pages through AJAX, discarding late responses from prior views. */
async function loadContent({ live = false } = {}) {
  if (!browseViews.includes(app.view)) return;
  contentRequest?.abort(); contentRequest = new AbortController();
  const signal = contentRequest.signal;
  const sequence = ++app.sequence; $('#content').setAttribute('aria-busy', 'true'); $('#pagination').hidden = true;
  if (!live) empty(tvViews.includes(app.view) ? 'Loading TV shows...' : t('status.loading'), tvViews.includes(app.view) ? 'Finding your next TV series...' : t('status.fetching'));
  $('#search-status').textContent = 'Searching...';
  try {
    if (!app.settings.hasTmdbToken && !['library', 'history', 'issues', 'tv-library', 'tv-history', 'tv-issues', 'tv-upcoming'].includes(app.view)) { empty(t('status.noTmdb'), t('status.noTmdbHelp')); return; }
    const parameters = new URLSearchParams({ ...app.filters, view: app.view, page: app.page, pageSize: app.preferences.pageSize,
      query: app.query, utcOffset: -new Date().getTimezoneOffset() });
    const television = tvViews.includes(app.view);
    const data = television ? await loadTvPage(signal) : await api('browse?' + parameters, undefined, { signal }); if (sequence !== app.sequence) return;
    app.pageData = data;
    const rows = television ? (['tv-history', 'tv-issues'].includes(app.view) ? data.plays : data.series) : ['history', 'issues'].includes(app.view) ? data.plays : data.movies;
    if (!rows.length) empty(television ? 'No matching TV results.' : t('status.noResults'), television ? 'Sync your TV sources or search for another series.' : data.incomplete ? t('status.remoteSearch') : t('status.noResultsHelp'));
    else if (television) { if (['tv-history', 'tv-issues'].includes(app.view)) renderTvHistory(rows); else renderTvSeries(rows); }
    else if (['history', 'issues'].includes(app.view)) renderHistory(rows, openMovie, openMatch, removeMovieHistory); else renderMovies(rows, openMovie);
    $('#result-count').textContent = data.totalResults.toLocaleString();
    $('#search-status').textContent = `${data.totalResults.toLocaleString()} results${app.query ? ' for ' + app.query : ''}.`;
    $('#feed-notice').textContent = data.notice ? television ? data.notice : t(data.notice === 'metadata' ? 'status.localFilterHelp' : 'status.remoteSearch') : app.query ? t('status.searchResults', { query: app.query }) : '';
    $('#pagination').hidden = data.totalPages <= 1 && !data.hasMore && app.page === 1;
    $('#page-number').textContent = t(data.hasMore && data.notice === 'remote' ? 'status.pageMore' : 'status.page', { page: data.page, pages: data.totalPages, count: data.totalResults.toLocaleString() });
    $('#previous').disabled = app.page <= 1; $('#next').disabled = !data.hasMore;
    $('#next span').textContent = t(data.incomplete ? 'action.keepSearching' : 'action.next');
  } catch (error) { if (error.name !== 'AbortError' && sequence === app.sequence) { empty(t('status.errorTitle'), error.message, true); $('#search-status').textContent = error.message; } }
  finally { if (sequence === app.sequence) $('#content').removeAttribute('aria-busy'); }
}

/** Removes all plays of one source item after explicit confirmation and refreshes watched status immediately. */
async function removeMovieHistory(play) {
  if (!window.confirm(`Remove all recorded plays of "${play.title}" from Elementarr? Future syncs will ignore this source item until restored in Administration. This changes Elementarr's history; Plex keeps its own records.`)) return;
  try { const result = await api('history/remove', { ratingKey: play.ratingKey });
    notify(`Removed ${result.removed} movie plays. This source item will stay out of future imports.`); await loadState(); await loadContent();
  } catch (error) { notify(error.message, true); }
}

/** Opens the right-hand Bootstrap drawer immediately, then adds rich TMDB artwork and credits. */
async function openMovie(movie) {
  app.selected = movie; const sequence = ++app.detailSequence;
  renderDetails(movie); updateMovieActions(); $('#add-error').textContent = '';
  await drawers.movie.show();
  const loading = document.createElement('p'); loading.className = 'detail-loading'; loading.textContent = t('status.detailLoading'); $('#movie-details').prepend(loading);
  try {
    const rich = await api(`movies/${movie.tmdbId}`);
    if (sequence !== app.detailSequence || !$('#movie-drawer').open) return;
    app.selected = rich; renderDetails(rich); updateMovieActions();
  } catch (error) { if (sequence === app.detailSequence) { loading.remove(); $('#add-error').textContent = error.message; } }
}

/** Shows destination and action availability for the selected movie without an implicit download search. */
function updateMovieActions() {
  const movie = app.selected; if (!movie) return;
  const destination = app.settings.rootFolder && app.settings.qualityProfileId;
  $('#confirm-add').disabled = movie.inRadarr || !destination;
  $('#confirm-add span').textContent = t(movie.inRadarr ? 'action.inRadarr' : 'action.add');
  $('#search-immediately').disabled = !movie.inRadarr && !destination;
  $('#add-destination').textContent = destination ? t('drawer.destination', { folder: app.settings.rootFolder, profile: app.settings.qualityProfileId }) : t('drawer.noDestination');
}

/** Reports Radarr command acknowledgement and subsequent status without equating a search with a completed download. */
async function watchCommand(command, title, notice) {
  const label = (status) => Object.hasOwn(ui.words, 'status.command.' + status.toLowerCase()) ? t('status.command.' + status.toLowerCase()) : t('status.command.unknown');
  for (let attempt = 0; attempt < 10; attempt++) {
    if (['completed', 'failed', 'aborted'].includes(command.status.toLowerCase())) break;
    await new Promise((resolve) => setTimeout(resolve, 3000));
    if ($('#app-view').hidden) return;
    try { command = await api(`radarr/commands/${command.commandId}`); }
    catch { notice.querySelector('span').textContent = t('status.commandUnknown'); return; }
    notice.querySelector('span').textContent = t('status.searchRunning', { title, status: label(command.status) });
  }
  if (['failed', 'aborted'].includes(command.status.toLowerCase())) { notice.classList.add('error'); notice.setAttribute('role', 'alert'); }
  if (!notice.isConnected && ['completed', 'failed', 'aborted'].includes(command.status.toLowerCase())) notify(t('status.searchRunning', { title, status: label(command.status) }), command.status.toLowerCase() !== 'completed');
}

/** Starts a manual correction flow for a specific unmatched source item. */
async function openMatch(play) {
  app.match = play; $('#match-original').textContent = t('drawer.matchYear', { title: play.title, year: play.year || t('status.tba') });
  $('#match-query').value = play.title.slice(0, 200); $('#match-results').replaceChildren();
  await drawers.match.show(); $('#match-query').focus(); await searchMatches();
}

/** Searches real TMDB candidates and requires an explicit choice before storing a persistent history correction. */
async function searchMatches() {
  const current = app.match; if (!current) return;
  $('#match-results').textContent = t('status.loading');
  try {
    const query = $('#match-query').value.trim(); if (!query) return;
    const result = await api(`discover?view=trending&query=${encodeURIComponent(query)}&page=1&hideOwned=false&hideWatched=false`);
    if (app.match !== current) return;
    $('#match-results').replaceChildren();
    for (const movie of result.movies) {
      const row = document.createElement('article'); row.className = 'match-result'; const image = imageUrl(movie.poster, 'w185');
      row.innerHTML = `${image ? `<img src="${image}" alt="" loading="lazy" referrerpolicy="no-referrer">` : '<i class="fa-solid fa-film" aria-hidden="true"></i>'}<div><h3>${escape(movie.title)}</h3><p class="field-help">${escape(movie.year || t('status.tba'))}</p><p>${escape(movie.overview.slice(0, 180))}</p><button class="btn-outline">${escape(t('drawer.matchConfirmation', { title: movie.title, year: movie.year || t('status.tba') }))}</button></div>`;
      row.querySelector('button').addEventListener('click', (event) => busy(event.currentTarget, async () => {
        try { const result = await api('history/match', { ratingKey: current.ratingKey, tmdbId: movie.tmdbId });
          notify(t('status.matched', { count: result.matched })); await drawers.match.hide(); app.match = null;
          await loadState(); await loadContent();
        } catch (error) { notify(error.message, true); }
      }));
      $('#match-results').append(row);
    }
    if (!result.movies.length) $('#match-results').textContent = t('drawer.noMatchResults'); protectImages($('#match-results'));
  } catch (error) { $('#match-results').textContent = error.message; }
}

/** Completes Plex's installation-owner PIN flow while all account tokens stay on the server. */
async function authorizePlex(connect) {
  const popup = window.open('about:blank', 'elementarr-plex-auth', 'width=850,height=730'); if (popup) popup.opener = null;
  const flow = ++app.plexFlow;
  try {
    if (connect) await saveConnections();
    const started = await api('plex/auth/start', { connect }); const url = new URL(started.authUrl);
    if (url.origin !== 'https://app.plex.tv' || url.pathname !== '/auth') throw new Error(t('error.plexAddress'));
    $('#plex-auth-link').href = started.authUrl; $('#plex-auth-progress').textContent = t('status.plexWaiting');
    $('#plex-auth-dialog').showModal(); if (popup) popup.location.replace(started.authUrl);
    for (let poll = 0; poll < 300 && flow === app.plexFlow; poll++) {
      await new Promise((resolve) => setTimeout(resolve, 2000)); if (flow !== app.plexFlow) return;
      const result = await api('plex/auth/poll', {}); if (result.status === 'pending') continue;
      $('#plex-auth-dialog').close(); popup?.close(); await session(); await loadSettings(); await loadFilterOptions();
      await navigate(new URLSearchParams(location.search).get('settings') === 'updates' ? 'admin' : connect || !app.settings.plexServerId ? 'settings' : 'for-you'); if (new URLSearchParams(location.search).get('settings') === 'updates') adminTab('updates'); notify(t(connect ? 'status.plexConnected' : 'status.plexSignedIn')); return;
    }
    if (flow === app.plexFlow) throw new Error(t('error.plexExpired'));
  } catch (error) { notify(error.message, true); }
  finally { popup?.close(); if (flow === app.plexFlow) $('#plex-auth-dialog').close(); }
}

/** Synchronizes independent sources and refreshes the active view without navigating the browser. */
async function synchronize(button) {
  await busy(button, async () => {
    $('#sync-status').textContent = t('status.syncing');
    try {
      const result = await api(`sync?source=${button.dataset.sync}`, {}); app.settings = await api('settings');
      const state = await loadState(); app.page = 1; if (browseViews.includes(app.view)) await loadContent();
      const names = [result.sonarrSynced ? 'Sonarr collection' : '', result.tvHistorySynced ? 'Plex TV history' : '', result.radarrSynced ? t('status.collectionSource') : '', result.historySynced ? t('status.historySource', { source: t(result.historySource === 'plex' ? 'connections.plex' : 'connections.tautulli') }) : ''].filter(Boolean);
      notify(t('status.synced', { sources: names.join(t('status.and')) }) + (state.warnings.length ? ' ' + t('status.reviewNotices') : ''), result.errors.length > 0);
    } catch (error) { await loadState().catch(() => {}); notify(error.message, true); }
  });
}


let connectionCheck;
/** Checks saved integrations automatically, retaining the last badges while requests are in flight. */
async function checkConnections() {
  if (connectionCheck) return connectionCheck;
  connectionCheck = (async () => {
    notify('Re-checking saved connections...');
    try {
      const result = await api('connections/check', {});
      for (const service of ['radarr', 'sonarr', 'tautulli', 'tmdb', 'plex']) {
        const badge = $('#' + service + '-status'); const configured = service === 'plex' ? app.settings.hasPlexToken : service === 'tautulli' ? app.settings.tautulliEnabled && app.settings.hasTautulliKey : service === 'radarr' ? app.settings.hasRadarrKey : service === 'sonarr' ? app.settings.hasSonarrKey : app.settings.hasTmdbToken;
        badge.textContent = t(result[service] ? 'status.connected' : configured ? 'status.attention' : service === 'tautulli' ? 'status.disabled' : 'status.notConfigured'); badge.className = `badge badge-subtle connection-status ${result[service] ? 'theme-success' : configured ? 'theme-warning' : 'theme-secondary'}`;
      }
      const matching = result.users.find((user) => user.name.toLowerCase() === app.settings.plexUsername.toLowerCase());
      if (app.settings.tautulliEnabled && result.tautulli) fillOptions('#plex-user', result.users, app.settings.plexUserId || matching?.id || '', t('status.chooseUser'));
      if (result.sonarr) { fillOptions('#sonarr-profile', result.sonarrProfiles, String(app.settings.sonarrQualityProfileId), t('status.loadProfiles')); fillOptions('#sonarr-root', result.sonarrFolders, app.settings.sonarrRootFolder, t('status.loadFolders')); }
      if (result.radarr) { fillOptions('#quality-profile', result.profiles, String(app.settings.qualityProfileId), t('status.loadProfiles')); fillOptions('#root-folder', result.folders, app.settings.rootFolder, t('status.loadFolders')); }
      $('#connection-results').textContent = result.errors.length ? result.errors.join('\n') : t('status.connectionsChecked');

      notify(result.errors.length ? 'Connections checked. Some sources need attention.' : 'Connections checked successfully.', result.errors.length > 0);
    } catch (error) { $('#connection-results').textContent = error.message; notify(error.message, true); }
    finally { connectionCheck = null; }
  })();
  return connectionCheck;
}

/** Loads the genre taxonomy for the filter controls. */
async function loadFilterOptions() {
  const result = await api('filter-options'); app.filterOptions = result; buildFilters(result.genres);
}

/** Wires authenticated actions, list sorting, AJAX pagination, timeline preferences, and responsive navigation. */
function bindEvents() {
  $('#server-form').addEventListener('submit', async (event) => {
    event.preventDefault(); await busy(event.submitter, async () => {
      try { await api('server', { port: Number($('#server-port').value), bindAddress: $('#server-bind').value, hostName: $('#server-host').value.trim() });
        $('#server-help').textContent = 'Saved. Use Restart service in the Elementarr tray menu to apply the change.'; notify('Server preferences saved. Restart Elementarr to apply.');
      } catch (error) { notify(error.message, true); }
    });
  });
  $('#logout').addEventListener('click', () => { $('#logout-dialog').showModal(); $('#cancel-logout').focus(); placeNotifications(); });
  $('#cancel-logout').addEventListener('click', () => $('#logout-dialog').close());
  $('#logout-dialog').addEventListener('close', placeNotifications);
  $('#confirm-logout').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try { app.plexFlow++; app.sequence++; clearTimeout(searchTimer); contentRequest?.abort(); await api('logout', {}); $('#logout-dialog').close(); $('#account-avatar').removeAttribute('src'); await session(); }
    catch (error) { notify(error.message, true); }
  }));
  $('#account-avatar').addEventListener('load', () => { $('#account-avatar').hidden = false; $('#account-avatar-fallback').hidden = true; });
  $('#account-avatar').addEventListener('error', () => { $('#account-avatar').hidden = true; $('#account-avatar-fallback').hidden = false; });
  document.querySelectorAll('[data-nav-group]').forEach((group) => group.addEventListener('toggle', () => {
    if (app.preferences.collapsed || !app.preferences.navGroups || app.preferences.navGroups[group.dataset.navGroup] === group.open) return;
    app.preferences.navGroups[group.dataset.navGroup] = group.open; savePreferences(app.preferences);
  }));
  document.querySelectorAll('[data-view]').forEach((button) => button.addEventListener('click', () => navigate(button.dataset.view)));
  $('#collapse-nav').addEventListener('click', () => { app.preferences.collapsed = !app.preferences.collapsed; applyPreferences(); savePreferences(app.preferences); });
  $('#search-form').addEventListener('submit', (event) => { event.preventDefault(); clearTimeout(searchTimer); void searchCurrentView(); });
  $('#search').addEventListener('input', (event) => { if (!event.isComposing) queueSearch(); });
  $('#search').addEventListener('compositionend', queueSearch);
  $('#open-filters').addEventListener('click', () => { writeFilters(); void drawers.filters.toggle(); });
  $('#filter-drawer').addEventListener('show.bs.drawer', () => { document.body.classList.add('filters-open'); $('#open-filters').setAttribute('aria-expanded', 'true'); });
  $('#filter-drawer').addEventListener('hidden.bs.drawer', () => { document.body.classList.remove('filters-open'); $('#open-filters').setAttribute('aria-expanded', 'false'); });
  $('#filter-form').addEventListener('submit', async (event) => { event.preventDefault(); if (!$('#filter-form').reportValidity()) return;
    app.filters = readFilters(); app.page = 1; updateFilterSummary(); if (window.innerWidth < 1100) await drawers.filters.hide(); await loadContent(); });
  $('#clear-filters').addEventListener('click', async () => { app.filters = { sort: $('#sort-by').value, order: $('#sort-order').value }; app.page = 1; writeFilters(); await loadContent(); });
  for (const id of ['sort-by', 'sort-order']) $('#' + id).addEventListener('change', async () => { app.filters.sort = $('#sort-by').value; app.filters.order = $('#sort-order').value; app.page = 1; await loadContent(); });
  $('#page-size').addEventListener('change', async () => { app.preferences.pageSize = Number($('#page-size').value); app.page = 1; savePreferences(app.preferences); await loadContent(); });
  $('#poster-size').addEventListener('input', () => { app.preferences.posterSize = Number($('#poster-size').value); applyPreferences(); savePreferences(app.preferences); });
  for (const mode of ['grid', 'list']) $('#' + mode + '-mode').addEventListener('click', () => { app.preferences.layout = mode; applyPreferences(); savePreferences(app.preferences); if (app.pageData) { if (tvViews.includes(app.view)) { if (!['tv-history', 'tv-issues'].includes(app.view)) renderTvSeries(app.pageData.series); } else if (!['history', 'issues'].includes(app.view)) renderMovies(app.pageData.movies, openMovie); } });
  $('#previous').addEventListener('click', async () => { if (app.page > 1) { app.page--; await loadContent(); } });
  $('#next').addEventListener('click', async () => { if (!app.pageData?.incomplete) app.page++; await loadContent(); });
  for (const direction of ['back', 'forward']) $('#timeline-' + direction).addEventListener('click', () => { const rail = $('#timeline-rail'); rail?.scrollBy({ left: (direction === 'forward' ? 1 : -1) * rail.clientWidth * .8, behavior: 'smooth' }); });
  $('#timeline-zoom').addEventListener('input', () => { const rail = $('#timeline-rail'); const ratio = rail ? rail.scrollLeft / app.preferences.timelineZoom : 0;
    app.preferences.timelineZoom = Number($('#timeline-zoom').value); applyPreferences(); if (rail) rail.scrollLeft = ratio * app.preferences.timelineZoom; savePreferences(app.preferences); });
  for (const [id, key] of [['timeline-posters', 'timelinePosters'], ['timeline-thumbs', 'timelineThumbnails'], ['timeline-ratings', 'timelineRatings']]) $('#' + id).addEventListener('change', () => { app.preferences[key] = $('#' + id).checked; applyTimelinePreferences(); savePreferences(app.preferences); });
  $('#connections-form').addEventListener('submit', async (event) => { event.preventDefault(); await busy($('#save-connections'), async () => { try { await saveConnections(); notify(t('status.connectionsSaved')); } catch (error) { notify(error.message, true); } }); });
  $('#check-connections').addEventListener('click', () => busy($('#check-connections'), async () => {
    try { await saveConnections(); await checkConnections(); } catch (error) { notify(error.message, true); }
  }));
  $('#refresh-movie-metadata').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try { await api('sync?source=radarr', {}); await api('metadata/refresh-matches', {}); notify('Refreshing movie metadata and matches in the background.'); } catch (error) { notify(error.message, true); }
  }));
  document.querySelectorAll('[data-sync]').forEach((button) => button.addEventListener('click', () => synchronize(button)));
  $('#tautulli-enabled').addEventListener('change', () => { $('#tautulli-fields').hidden = !$('#tautulli-enabled').checked; });
  $('#plex-login').addEventListener('click', (event) => busy(event.currentTarget, () => authorizePlex(false)));
  $('#connect-plex').addEventListener('click', (event) => busy(event.currentTarget, () => authorizePlex(true)));
  const cancelPlex = () => { app.plexFlow++; $('#plex-auth-dialog').close(); void api('plex/auth/cancel', {}).catch(() => {}); };
  $('#cancel-plex-auth').addEventListener('click', cancelPlex); $('#plex-auth-dialog').addEventListener('cancel', cancelPlex);
  $('#movie-drawer').addEventListener('hidden.bs.drawer', () => { app.detailSequence++; });
  document.querySelectorAll('dialog').forEach((dialog) => dialog.addEventListener('close', placeNotifications));
  $('#confirm-add').addEventListener('click', async () => { const movie = app.selected; if (!movie || movie.inRadarr) return;
    await busy($('#confirm-add'), async () => { try { await api(`radarr/${movie.tmdbId}`, { searchNow: false }); app.selected = { ...movie, inRadarr: true };
      // Close the detail drawer only after Radarr confirms the movie was added.
      await drawers.movie.hide();
      notify(t('status.added', { title: movie.title })); await loadState(); await loadContent();
    } catch (error) { $('#add-error').textContent = error.message; notify(error.message, true); } }); updateMovieActions(); });
  $('#search-immediately').addEventListener('click', async () => { const movie = app.selected; if (!movie) return;
    await busy($('#search-immediately'), async () => { try { const command = await api(`radarr/${movie.tmdbId}/search`, {}); app.selected = { ...movie, inRadarr: true };
      const status = t(Object.hasOwn(ui.words, 'status.command.' + command.status.toLowerCase()) ? 'status.command.' + command.status.toLowerCase() : 'status.command.unknown');
      const notice = notify(t('status.searchSent', { title: movie.title, status }), false, true); void watchCommand(command, movie.title, notice);
      await loadState(); await loadContent();
    } catch (error) { $('#add-error').textContent = error.message; notify(t('status.searchFailed', { title: movie.title, message: error.message }), true); } }); updateMovieActions(); });
  $('#match-form').addEventListener('submit', async (event) => { event.preventDefault(); await busy(event.submitter, searchMatches); });
  document.addEventListener('elementarr:filters-applied', () => { app.page = 1; void loadContent(); });
  document.addEventListener('elementarr:session-expired', () => { void session(); });
  document.addEventListener('elementarr:appearance-changed', () => { applyTexts(); updateHeading(); buildSortOptions(); if (app.filterOptions) buildFilters(app.view.startsWith('tv-') ? tvGenres : app.filterOptions.genres); applyPreferences(); });
  document.addEventListener('elementarr:rating-imported', () => { void loadFilterOptions(); });
  bindTv(async (settingsChanged) => { if (settingsChanged) await loadSettings(); await loadState(); if (browseViews.includes(app.view)) await loadContent(); });
  $('#restore-movie-history').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    if (!window.confirm('Allow all removed movie-history items to be imported again on the next movie-history sync?')) return;
    try { await api('history/restore-removed', {}); notify('Removed movie-history items can return on the next history sync.'); } catch (error) { notify(error.message, true); }
  }));
  bindUpdates();
  bindAdmin();
}

/** Starts the existing local/LAN app using only configured providers and saved display preferences. */
async function start() {
  registerTexts(); bindEvents();
  try { await loadAppearance(); initPreferences(); if (await session()) { await loadSettings(); await loadFilterOptions(); await navigate(new URLSearchParams(location.search).get('settings') === 'updates' ? 'admin' : 'for-you'); if (new URLSearchParams(location.search).get('settings') === 'updates') adminTab('updates'); } }
  catch (error) { $('#login-view').hidden = false; $('#login-error').textContent = t('error.start', { message: error.message }); }
}

void start();
