import { Drawer } from './vendor/bootstrap/bootstrap.bundle.min.js';
import { $, app, api, busy, escape, imageUrl, notify, number } from './core.js';
import { ui, t } from './ui.js';
import { protectImages, applyTimelinePreferences, bindTimelineDrag } from './movies.js';

export const tvViews = ['tv-for-you', 'tv-upcoming', 'tv-trending', 'tv-top-rated', 'tv-library', 'tv-history', 'tv-issues'];
for (const [view, title, caption, description] of [
  ['tv-for-you', 'Your next great series.', 'TV for you', 'Recommendations from your watched TV series. Trending TV appears until your first matched episode sync.'],
  ['tv-upcoming', 'Make room for the next episode.', 'Episodes Airing Soon', 'Episodes airing in the next 14 days from your Sonarr collection, in your local time.'],
  ['tv-issues', 'Bring every episode into focus.', 'Affected TV History', 'Episodes without a series identifier. Correct the parent series in Plex, then refresh TV metadata. Movie matching stays separate.'],
  ['tv-trending', 'What is on everyone’s screen?', 'Trending TV', 'Discover this week’s trending television.'],
  ['tv-top-rated', 'The series that stay with you.', 'Top rated TV', 'Explore highly rated television from TMDB.'],
  ['tv-library', 'Your television collection.', 'TV collection', 'Series managed by Sonarr, with episode file counts.'],
  ['tv-history', 'Your story, episode by episode.', 'Watched TV History', 'Your Plex episode watches. Movie history is stored and displayed separately.']
]) Object.assign(ui.words, { [`view.${view}.eyebrow`]: 'YOUR TELEVISION', [`view.${view}.title`]: title,
  [`view.${view}.caption`]: caption, [`view.${view}.description`]: description });

let drawer;
let selected;
let sequence = 0;

/** Uses TMDB artwork when available, otherwise a credential-free authenticated Sonarr/Plex image proxy. */
function tvImage(value) {
  return imageUrl(value.poster) || (value.sonarrId > 0 ? `/api/tv/art/sonarr/${value.sonarrId}`
    : /^\d+$/.test(value.seriesKey || '') ? `/api/tv/art/plex/${value.seriesKey}` : null);
}

/** Connects the separate TV drawer, Sonarr confirmation, and Plex server selector. */
export function bindTv(refresh) {
  drawer = new Drawer($('#tv-drawer'));
  $('#refresh-tv-metadata').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try { await api('tv/metadata/refresh', {}); notify('Refreshing TV metadata and episode matches in the background.'); } catch (error) { notify(error.message, true); }
  }));
  let wasRefreshing = false;
  setInterval(async () => {
    if ($('#app-view').hidden || document.hidden || !tvViews.includes(app.view)) return;
    try {
      const status = await api('tv/metadata/status');
      $('#tv-refresh-status').textContent = status.running ? status.stage + '...' : status.error || '';
      if (wasRefreshing && !status.running) await refresh();
      wasRefreshing = status.running;
    } catch { /* A restart or sign-out is handled by the normal session flow. */ }
  }, 5000);
  $('#tv-drawer').addEventListener('hidden.bs.drawer', () => { sequence++; selected = null; });
  $('#tv-add').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    if (!selected || selected.inSonarr) return;
    try {
      const search = $('#tv-search-now').checked;
      await api(`sonarr/${selected.tmdbId}`, { searchNow: search });
      notify(`${selected.title} added to Sonarr.${search ? ' Sonarr accepted the request to search for missing episodes.' : ''}`);
      await drawer.hide(); await refresh();
    } catch (error) { $('#tv-add-error').textContent = error.message; }
  }));
  $('#load-plex-servers').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try {
      const servers = await api('plex/servers'); const select = $('#plex-server');
      select.replaceChildren(new Option('Choose your owned Plex server', ''));
      for (const server of servers) select.add(new Option(server.name, server.id));
      select.value = app.settings.plexServerId || '';
      if (!servers.length) notify('This Plex account advertises no owned servers. Claim a Plex server and refresh this list.', true);
    } catch (error) { notify(error.message, true); }
  }));
  $('#select-plex-server').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try {
      await api('plex/server', { serverId: $('#plex-server').value });
      notify('Plex server verified and selected. You can now sync movie and TV history separately.');
      await refresh(true);
    } catch (error) { notify(error.message, true); }
  }));
}

/** Opens TV details using TV identifiers and Sonarr actions only. */
export async function openTv(series) {
  const current = ++sequence; selected = series;
  $('#tv-title').textContent = series.title;
  $('#tv-details').textContent = 'Loading TV series details…'; $('#tv-add-error').textContent = '';
  $('#tv-add').disabled = true; $('#tv-search-now').checked = false;
  await drawer.show();
  try {
    if (series.tmdbId) series = await api(`tv/${series.tmdbId}`);
    else if (series.tvdbId) series = await api(`tv/sonarr/${series.tvdbId}`);
    if (current !== sequence) return;
    selected = series; $('#tv-title').textContent = series.title;
    const poster = tvImage(series);
    const genres = (series.facts?.genres || []).map((genre) => `<span class="badge badge-outline">${escape(genre)}</span>`).join('');
    $('#tv-details').innerHTML = `${poster ? `<img class="detail-poster" src="${poster}" alt="" referrerpolicy="no-referrer">` : ''}<p class="eyebrow">${escape(series.year || 'Year unavailable')}</p><div class="genre-tags">${genres}</div><p>${escape(series.overview || 'No synopsis available.')}</p><p class="field-help">TMDB: ${escape(number(series.score))} / 10${series.inSonarr ? ` · ${series.episodeFileCount} episode files in Sonarr` : ''}</p>${series.facts ? `<p class="field-help">${escape(series.facts.status)} | ${series.facts.seasons} seasons | ${series.facts.episodes} episodes</p>` : ''}${series.hasWatchedEpisodes ? '<p>Has watched episodes. This does not mean the entire series is complete.</p>' : ''}${series.tmdbId ? `<a href="https://www.themoviedb.org/tv/${series.tmdbId}" target="_blank" rel="noopener noreferrer">View series on TMDB</a>` : '<p>Sync Plex history after adding a TMDB token to resolve this series.</p>'}`;
    $('#tv-add').disabled = series.inSonarr || !series.tmdbId || !app.settings.hasSonarrKey || !app.settings.sonarrQualityProfileId || !app.settings.sonarrRootFolder;
    $('#tv-search-now').disabled = $('#tv-add').disabled;
    $('#tv-add span').textContent = series.inSonarr ? 'In Sonarr' : 'Add to Sonarr';
    $('#tv-destination').textContent = app.settings.sonarrRootFolder ? `Sonarr destination: ${app.settings.sonarrRootFolder} · Profile ${app.settings.sonarrQualityProfileId}` : 'Choose a Sonarr quality profile and root folder in Connections first.';
    protectImages($('#tv-details'));
  } catch (error) { if (current === sequence) $('#tv-add-error').textContent = error.message; }
}

/** Renders TV series cards with explicit series and episode availability rather than movie status. */
export function renderTvSeries(series) {
  const grid = document.createElement('div'); grid.className = `movie-grid${app.preferences.layout === 'list' ? ' list-layout' : ''}`;
  for (const show of series) {
    const card = document.createElement('article'); card.className = 'movie-card'; const poster = tvImage(show);
    card.innerHTML = `<button class="poster-button" aria-label="${escape(show.title)}"><span class="poster-fallback"><i class="fa-solid fa-tv" aria-hidden="true"></i></span>${poster ? `<img src="${poster}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}</button><div class="movie-info"><button class="movie-title-button"><h3>${escape(show.title)}</h3></button><p class="movie-meta"><span>${escape(show.year || 'TBA')} · TMDB ${escape(number(show.score))}</span><button class="movie-add badge ${show.inSonarr ? 'badge-subtle' : 'badge-outline'} theme-primary"><i class="fa-solid ${show.inSonarr ? 'fa-check' : 'fa-plus'}" aria-hidden="true"></i>${show.inSonarr ? 'In Sonarr' : 'Add to Sonarr'}</button></p><p class="field-help">${show.inSonarr ? `In Sonarr · ${show.episodeFileCount} episode files` : 'Add to Sonarr'}</p>${show.hasWatchedEpisodes ? '<p class="field-help">Has watched episodes</p>' : ''}<p class="movie-reason">${escape(show.reason)}</p><p class="list-overview">${escape(show.overview)}</p></div>`;
    card.querySelectorAll('button').forEach((button) => button.addEventListener('click', () => openTv(show)));
    if (show.airingEpisode) {
      const airing = document.createElement('p'); airing.className = 'episode-airing field-help';
      airing.textContent = `S${String(show.airingEpisode.season).padStart(2, '0')}E${String(show.airingEpisode.episode).padStart(2, '0')} | ${show.airingEpisode.title} | ${new Date(show.airingEpisode.airsAt).toLocaleString()}`;
      card.querySelector('.movie-info').append(airing);
    }
    grid.append(card);
  }
  $('#content').replaceChildren(grid); protectImages(grid);
}

/** Builds an episode-only timeline; unmatched episodes never open movie matching or movie details. */
export function renderTvHistory(plays) {
  const rail = document.createElement('div'); rail.className = 'timeline-rail'; rail.id = 'timeline-rail';
  rail.tabIndex = 0; rail.setAttribute('role', 'list'); rail.setAttribute('aria-label', 'Watched TV History');
  for (const play of plays) {
    const card = document.createElement('article'); card.className = 'timeline-item'; card.setAttribute('role', 'listitem');
    const date = new Date(play.watchedAt * 1000); const poster = tvImage(play);
    const label = `${play.season == null ? 'Season ?' : 'S' + String(play.season).padStart(2, '0')} ${play.episode == null ? 'Episode ?' : 'E' + String(play.episode).padStart(2, '0')}`;
    card.innerHTML = `<div class="timeline-date"><span class="timeline-dot"></span><time datetime="${date.toISOString()}">${escape(date.toLocaleDateString())}</time><small>${escape(date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }))}</small></div><div class="timeline-card"><button class="timeline-open"><div class="timeline-art"><span class="timeline-art-fallback"><i class="fa-solid fa-tv" aria-hidden="true"></i></span>${poster ? `<img class="timeline-poster" src="${poster}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}</div><div class="timeline-copy"><span class="badge badge-subtle theme-primary">Watched episode</span><h3>${escape(play.seriesTitle)}</h3><p>${escape(label)}</p><p>${escape(play.episodeTitle)}</p>${!play.tmdbId ? '<p class="field-help">Series TMDB match unavailable</p>' : ''}</div></button></div>`;
    card.querySelector('button').addEventListener('click', () => openTv({ tmdbId: play.tmdbId, tvdbId: play.tvdbId, seriesKey: play.seriesKey, sonarrId: play.sonarrId, title: play.seriesTitle, poster: play.poster }));
    rail.append(card);
  }
  $('#content').replaceChildren(rail); protectImages(rail); applyTimelinePreferences(); bindTimelineDrag(rail);
}

/** Fetches TV pages independently of movie filters and movie pagination state. */
export async function loadTvPage(signal) {
  const params = new URLSearchParams({ view: app.view, query: app.query, page: app.page,
    pageSize: app.preferences.pageSize, sort: $('#tv-sort').value, order: $('#tv-order').value });
  return api('tv/browse?' + params, undefined, { signal });
}
