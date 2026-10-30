import { Drawer } from './vendor/bootstrap/bootstrap.bundle.min.js';
import { $, app, api, busy, escape, imageUrl, notify } from './core.js';
import { ui } from './ui.js';
import { protectImages, ratingStrip, renderDetails, renderHistory } from './movies.js';

export const tvViews = ['tv-for-you', 'tv-upcoming', 'tv-trending', 'tv-top-rated', 'tv-library', 'tv-history', 'tv-issues'];
for (const [view, title, caption, description] of [
  ['tv-for-you', 'Your next great series.', 'TV for you', 'Recommendations from your watched TV series. Trending TV appears until your first matched episode sync.'],
  ['tv-upcoming', 'Make room for the next episode.', 'Episodes Airing Soon', 'Episodes airing in the next 14 days from your Sonarr collection, in your local time.'],
  ['tv-issues', 'Bring every episode into focus.', 'Affected TV History', 'Episodes without a series identifier. Correct the parent series in Plex, then refresh TV metadata.'],
  ['tv-trending', 'What is on everyone’s screen?', 'Trending TV', 'Discover this week’s trending television.'],
  ['tv-top-rated', 'The series that stay with you.', 'Top rated TV', 'Explore highly rated television from TMDB.'],
  ['tv-library', 'Your television collection.', 'TV collection', 'Series managed by Sonarr, with episode file counts.'],
  ['tv-history', 'Your story, episode by episode.', 'Watched TV History', 'Your Plex episode watches.']
]) Object.assign(ui.words, { [`view.${view}.eyebrow`]: 'YOUR TELEVISION', [`view.${view}.title`]: title,
  [`view.${view}.caption`]: caption, [`view.${view}.description`]: description });

let drawer;
let selected;
let sequence = 0;

/** Uses TMDB artwork or a credential-free authenticated Sonarr/Plex show-poster proxy. */
function tvImage(value) {
  return imageUrl(value.poster) || (value.sonarrId > 0 ? `/api/tv/art/sonarr/${value.sonarrId}`
    : /^[1-9]\d*$/.test(value.seriesKey || '') ? `/api/tv/art/plex/${value.seriesKey}` : null);
}

/** Formats episode numbers without turning missing numbers into season or episode zero. */
function episodeLabel(value) {
  return `${value.season == null ? 'Season ?' : 'S' + String(value.season).padStart(2, '0')} ${value.episode == null ? 'Episode ?' : 'E' + String(value.episode).padStart(2, '0')}`;
}

/** Adapts TV facts for shared presentation while keeping episode and show ratings distinct. */
function tvMedia(series, watch) {
  const show = series.facts || {}; const episode = watch?.details; const facts = episode?.facts || {};
  const thumbnail = imageUrl(facts.backdrop) ? facts.backdrop : (/^[1-9]\d*$/.test(watch?.ratingKey || '') ? `/api/tv/art/plex-episode/${watch.ratingKey}` : null);
  return { ...series, mediaType: 'tv', poster: imageUrl(series.poster) ? series.poster : tvImage({ ...series, seriesKey: watch?.seriesKey || series.seriesKey }),
    title: watch?.seriesTitle || series.title, year: episode?.airDate ? Number(episode.airDate.slice(0, 4)) : series.year,
    releaseDate: episode?.airDate, overview: watch ? episode?.overview || 'No episode synopsis available.' : series.overview,
    watched: Boolean(watch), available: !watch && series.episodeFileCount > 0,
    score: watch ? facts.tmdb : series.score,
    facts: watch ? { ...show, ...facts, backdrop: thumbnail, logo: show.logo, tagline: '',
      runtime: facts.runtime || show.runtime, genres: show.genres, language: show.language,
      certification: show.certification, countries: show.countries, studios: show.studios,
      cast: facts.cast?.length ? facts.cast : show.cast, imdb: facts.imdb, rottenTomatoes: facts.rottenTomatoes,
      tmdb: facts.tmdb, votes: facts.votes, imdbId: facts.imdbId, ratingOrigin: facts.ratingOrigin || 'Plex / TMDB'
    } : show };
}

/** Connects TV details, explicit Sonarr actions, metadata refresh, and Plex server selection. */
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
  for (const [id, search] of [['tv-add', false], ['tv-add-search', true]]) $('#' + id).addEventListener('click', (event) => busy(event.currentTarget, async () => {
    if (!selected || selected.inSonarr) return;
    const show = selected;
    $('#tv-add').disabled = true; $('#tv-add-search').disabled = true;
    try {
      await api(`sonarr/${show.tmdbId}`, { searchNow: search });
      notify(`${show.title} added to Sonarr.${search ? ' Sonarr accepted the request to search for missing episodes.' : ''}`);
      await drawer.hide(); await refresh();
    } catch (error) { $('#tv-add-error').textContent = error.message; }
    finally { if (selected === show) updateTvActions(); }
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
      notify('Plex server verified and selected. Sync History imports movie and TV history.'); await refresh(true);
    } catch (error) { notify(error.message, true); }
  }));
}

/** Shows the selected destination and enables only supported, explicitly requested Sonarr actions. */
function updateTvActions() {
  const disabled = !selected || selected.inSonarr || !selected.tmdbId || !app.settings.hasSonarrKey || !app.settings.sonarrQualityProfileId || !app.settings.sonarrRootFolder;
  $('#tv-add').disabled = disabled; $('#tv-add-search').disabled = disabled;
  $('#tv-add span').textContent = selected?.inSonarr ? 'In Sonarr' : 'Add to Sonarr';
  $('#tv-destination').textContent = app.settings.sonarrRootFolder ? `Sonarr destination: ${app.settings.sonarrRootFolder} · Profile ${app.settings.sonarrQualityProfileId}` : 'Choose a Sonarr quality profile and root folder in Connections first.';
}

/** Uses the movie detail composition for show and episode drawers with TV links and metadata. */
function renderTvDetails(series, watch) {
  renderDetails(tvMedia(series, watch), { container: $('#tv-details'), mediaType: 'tv',
    episode: watch?.season != null && watch?.episode != null ? watch : null,
    subtitle: watch ? `${episodeLabel(watch)} · ${watch.details?.title || watch.episodeTitle}` : '',
    extraFacts: watch ? [['Series', series.title], ['Episode', episodeLabel(watch)]]
      : [['Status', series.facts?.status], ['Seasons', series.facts?.seasons], ['Episodes', series.facts?.episodes]] });
  if (!watch) renderSeasons(series);
}

/** Shows completion only when an episode total is known, with a native accessible progress meter. */
function progressMarkup(show) {
  if (!Number.isFinite(show.completionPercent)) return '';
  const percent = Math.max(0, Math.min(100, show.completionPercent));
  return `<span class="show-progress"><strong>${percent}% Complete</strong><progress value="${percent}" max="100" aria-label="${escape(show.title)}: ${percent}% complete">${percent}%</progress></span>`;
}

/** Adds lazily loaded, expandable seasons to the shared rich detail card. */
function renderSeasons(show) {
  const section = document.createElement('section'); section.className = 'tv-seasons detail-section';
  section.innerHTML = `<h3>Seasons &amp; episodes</h3><p class="field-help">${show.watchedEpisodeCount || 0} distinct regular episodes watched${Number.isFinite(show.completionPercent) ? ` · ${show.completionPercent}% complete` : ''}. Progress counts known regular episodes; specials are listed separately.</p>`;
  const current = sequence;
  for (const season of show.facts?.seasonList || []) {
    const item = document.createElement('details'); item.className = 'tv-season';
    item.innerHTML = `<summary>${escape(season.name || (season.number === 0 ? 'Specials' : 'Season ' + season.number))}<small>${season.episodeCount} episodes</small></summary><div class="tv-season-content"></div>`;
    let loaded = false; let pending = false;
    item.addEventListener('toggle', async () => {
      if (!item.open || loaded || pending) return;
      pending = true;
      const body = item.querySelector('.tv-season-content'); body.innerHTML = '<p class="field-help" role="status">Loading episodes...</p>';
      try {
        const route = show.tmdbId ? `tv/${show.tmdbId}` : `tv/sonarr/${show.tvdbId}`;
        const episodes = await api(`${route}/season/${season.number}`);
        if (current !== sequence || !item.isConnected) return;
        body.innerHTML = episodes.length ? `<ol class="tv-episodes">${episodes.map((episode) => `<li class="tv-episode">${imageUrl(episode.still, 'w185') ? `<img src="${imageUrl(episode.still, 'w185')}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}<span class="tv-episode-copy"><strong>${escape(episodeLabel(episode))} · ${escape(episode.title || 'Untitled episode')}</strong><small>${escape(episode.airDate || 'Air date unavailable')}</small></span><span class="${episode.watched ? 'watched' : 'muted'}"><i class="fa-solid ${episode.watched ? 'fa-circle-check' : 'fa-circle'}" aria-hidden="true"></i> ${episode.watched ? 'Watched' : 'Unwatched'}</span></li>`).join('')}</ol>` : '<p class="field-help">No episode metadata is available for this season.</p>';
        loaded = true; protectImages(body);
      } catch (error) {
        if (current !== sequence) return;
        body.replaceChildren(); const message = document.createElement('p'); message.className = 'field-help'; message.textContent = error.message + ' Close and reopen this season to retry.'; body.append(message);
      } finally { pending = false; }
    });
    section.append(item);
  }
  if (!show.facts?.seasonList?.length) { const message = document.createElement('p'); message.className = 'field-help'; message.textContent = 'Season metadata is unavailable. Connect TMDB or refresh TV metadata in Connections.'; section.append(message); }
  const main = $('#tv-details .movie-detail-main');
  main.insertBefore(section, main.querySelector('.cast-grid')?.closest('section') || null);
}

/** Opens cached TV details immediately and rejects stale responses after another card is opened. */
export async function openTv(series, watch = null) {
  const current = ++sequence;
  if (!watch && series.airingEpisode) watch = { ...series.airingEpisode, seriesTitle: series.title, episodeTitle: series.airingEpisode.title };
  selected = series; renderTvDetails(series, watch); updateTvActions(); $('#tv-add-error').textContent = '';
  $('#tv-add').disabled = true; $('#tv-add-search').disabled = true;
  const loading = document.createElement('p'); loading.className = 'detail-loading'; loading.textContent = 'Loading the full TV story…'; $('#tv-details').prepend(loading);
  await drawer.show();
  try {
    if (series.tmdbId) series = await api(`tv/${series.tmdbId}`);
    else if (series.tvdbId && series.inSonarr) series = await api(`tv/sonarr/${series.tvdbId}`);
    if (current !== sequence) return;
    selected = series;
    if (watch && series.tmdbId && watch.season != null && watch.episode != null) {
      try {
        const details = await api(`tv/${series.tmdbId}/season/${watch.season}/episode/${watch.episode}`);
        if (details) watch = { ...watch, details: { ...details, facts: { ...details.facts,
          imdb: watch.details?.facts?.imdb, rottenTomatoes: watch.details?.facts?.rottenTomatoes,
          ratingOrigin: watch.details?.facts?.ratingOrigin || details.facts.ratingOrigin } } };
      } catch (error) { if (current === sequence) $('#tv-add-error').textContent = error.message; }
    }
    if (current !== sequence) return;
    renderTvDetails(series, watch); updateTvActions();
  } catch (error) { if (current === sequence) { loading.remove(); $('#tv-add-error').textContent = error.message; updateTvActions(); } }
}

/** Renders TV series cards using the movie rating strip and metadata hierarchy. */
export function renderTvSeries(series) {
  const grid = document.createElement('div'); grid.className = `movie-grid${app.preferences.layout === 'list' ? ' list-layout' : ''}`;
  for (const show of series) {
    const card = document.createElement('article'); card.className = 'movie-card'; const poster = tvImage(show);
    card.innerHTML = `<button class="poster-button" aria-label="${escape(show.title)}"><span class="poster-fallback"><i class="fa-solid fa-tv" aria-hidden="true"></i></span>${poster ? `<img src="${poster}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}${ratingStrip(tvMedia(show))}</button><div class="movie-info"><button class="movie-title-button"><h3>${escape(show.title)}</h3></button><div class="movie-meta"><span>${escape(show.year || 'TBA')}${show.facts?.runtime ? ` · ${show.facts.runtime} min` : ''}</span><button class="movie-add badge ${show.inSonarr ? 'badge-subtle' : 'badge-outline'} theme-primary"><i class="fa-solid ${show.inSonarr ? 'fa-check' : 'fa-plus'}" aria-hidden="true"></i>${show.inSonarr ? 'In Sonarr' : 'Add to Sonarr'}</button></div><p class="movie-genres">${escape((show.facts?.genres || []).slice(0, 3).join(' · '))}</p><p class="field-help">${show.inSonarr ? `${show.episodeFileCount} episode files in Sonarr` : ''}</p>${show.hasWatchedEpisodes ? '<p class="field-help">Has watched episodes</p>' : ''}<p class="movie-reason">${escape(show.reason)}</p><p class="list-overview">${escape(show.overview)}</p></div>`;
    card.querySelector('.poster-button').insertAdjacentHTML('beforeend', progressMarkup(show));
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

/** Reuses movie timeline cards with episode stills, show posters, ratings and episode details. */
export function renderTvHistory(plays) {
  const rows = plays.map((watch) => {
    const series = watch.series || { tmdbId: watch.tmdbId, tvdbId: watch.tvdbId, seriesKey: watch.seriesKey,
      sonarrId: watch.sonarrId, title: watch.seriesTitle, poster: watch.poster };
    return { play: { ...watch, title: watch.seriesTitle, episodeTitle: watch.details?.title || watch.episodeTitle, year: watch.details?.airDate?.slice(0, 4) || series.year,
      completed: true, episodeLabel: episodeLabel(watch) }, movie: { ...tvMedia(series, watch), series, watch } };
  });
  renderHistory(rows, (media) => openTv(media.series, media.watch), undefined, undefined, { mediaType: 'tv', caption: 'Watched TV History' });
}

/** Fetches TV pages using the active view's independent extensive filters and pagination. */
export async function loadTvPage(signal) {
  const params = new URLSearchParams({ ...app.filters, view: app.view, query: app.query, page: app.page,
    pageSize: app.preferences.pageSize, utcOffset: -new Date().getTimezoneOffset() });
  return api('tv/browse?' + params, undefined, { signal });
}
