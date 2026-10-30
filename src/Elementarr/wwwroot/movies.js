import { $, app, escape, imageUrl, number } from './core.js';
import { t, ui } from './ui.js';

Object.assign(ui.words, { 'movie.because': 'Because of {title}', 'tv.available': 'Episode files in Sonarr', 'tv.watched': 'Watched episode', 'tv.partlyWatched': 'Has watched episodes', 'tv.managed': 'In Sonarr' });

/** Accepts TMDB artwork or an authenticated, source-scoped local TV artwork path. */
export function mediaImage(value, size = 'w500') {
  return /^\/api\/tv\/art\/(sonarr|plex|plex-episode)\/[1-9]\d*$/.test(value || '') ? value : imageUrl(value, size);
}

/** Localizes the application's recommendation template while keeping the source movie title unchanged. */
function recommendationReason(reason) {
  return reason.startsWith('Because of ') ? t('movie.because', { title: reason.slice(11) }) : reason;
}

/** Renders labeled rating values with each source's native scale and accessible availability/watch icons. */
export function ratingStrip(movie, extraClass = '') {
  const facts = movie.facts || {};
  const scores = [['imdb', 'source.imdb', facts.imdb, 10], ['rt', 'source.rt', facts.rottenTomatoes, 100],
    ['tmdb', 'source.tmdb', facts.tmdb ?? (movie.score > 0 ? movie.score : null), 10]];
  const states = (movie.mediaType === 'tv' ? [[movie.available, 'fa-hard-drive', 'tv.available'], [movie.watched, 'fa-check-double', 'tv.watched'], [movie.hasWatchedEpisodes && !movie.watched, 'fa-check-double', 'tv.partlyWatched'], [movie.inSonarr && !movie.available, 'fa-tv', 'tv.managed']] : [[movie.available, 'fa-hard-drive', 'status.available'], [movie.watched, 'fa-check-double', 'status.watched'], [movie.inRadarr && !movie.available, 'fa-clapperboard', 'status.managed']]).filter(([show]) => show);
  const chips = scores.map(([source, label, value, scale]) => {
    const fullLabel = source === 'rt' ? 'source.rtLong' : label;
    const description = t(fullLabel) + ': ' + (value == null ? t('status.unrated') : number(value, source === 'rt' ? 0 : 1) + ' / ' + scale);
    return `<span class="rating-chip ${source}" role="img" aria-label="${escape(description)}" title="${escape(description)}"><small>${escape(t(label))}</small><strong>${escape(number(value, source === 'rt' ? 0 : 1))}${source === 'rt' && value != null ? '<sup>%</sup>' : ''}</strong></span>`;
  }).join('');
  return `<div class="poster-meta-strip ${extraClass}"><span class="poster-state-icons">${states.map(([, icon, key]) => `<span role="img" title="${escape(t(key))}" aria-label="${escape(t(key))}"><i class="fa-solid ${icon}" aria-hidden="true"></i></span>`).join('')}</span>${chips}</div>`;
}

/** Displays the collection/discovery grid with persistent layout preferences and explicit Bootstrap 6 action labels. */
export function renderMovies(movies, openMovie) {
  const grid = document.createElement('div'); grid.className = `movie-grid${app.preferences.layout === 'list' ? ' list-layout' : ''}`;
  for (const movie of movies) {
    const card = document.createElement('article'); card.className = 'movie-card';
    const poster = imageUrl(movie.poster);
    const runtime = movie.facts?.runtime ? t('status.minutes', { count: movie.facts.runtime }) : '';
    card.innerHTML = `<button class="poster-button" aria-label="${escape(t('action.details', { title: movie.title }))}"><span class="poster-fallback"><i class="fa-solid fa-film" aria-hidden="true"></i></span>${poster ? `<img src="${poster}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}${ratingStrip(movie)}</button><div class="movie-info"><button class="movie-title-button"><h3>${escape(movie.title)}</h3></button><div class="movie-meta"><span>${escape(movie.year || t('status.tba'))}${runtime ? ` · ${escape(runtime)}` : ''}</span><button class="movie-add badge ${movie.inRadarr ? 'badge-subtle' : 'badge-outline'} theme-primary" aria-label="${escape(t(movie.inRadarr ? 'status.managed' : 'action.add'))}"><i class="fa-solid ${movie.inRadarr ? 'fa-check' : 'fa-plus'}" aria-hidden="true"></i><span>${escape(t(movie.inRadarr ? 'action.inRadarr' : 'connections.radarr'))}</span></button></div><p class="movie-genres">${escape((movie.facts?.genres || []).slice(0, 3).join(' · '))}</p>${movie.reason ? `<p class="movie-reason">${escape(movie.reason)}</p>` : ''}<p class="list-overview">${escape(movie.overview)}</p></div>`;
    card.querySelectorAll('button').forEach((button) => button.addEventListener('click', () => openMovie(movie)));
    const reason = card.querySelector('.movie-reason');
    if (reason) reason.textContent = recommendationReason(movie.reason);
    grid.append(card);
  }
  $('#content').replaceChildren(grid); protectImages(grid);
}

/** Builds a horizontal, keyboard- and pointer-scrollable timeline with independent poster, thumbnail, and ratings toggles. */
export function renderHistory(items, openMovie, openMatch, removeHistory, options = {}) {
  const rail = document.createElement('div'); rail.className = 'timeline-rail'; rail.id = 'timeline-rail';
  rail.tabIndex = 0; rail.setAttribute('role', 'list'); rail.setAttribute('aria-label', options.caption || t('view.history.caption'));
  for (const item of items) {
    const play = item.play; const movie = item.movie;
    const date = new Date(play.watchedAt * 1000);
    const card = document.createElement('article'); card.className = 'timeline-item'; card.setAttribute('role', 'listitem');
    const poster = mediaImage(movie?.poster); const backdrop = mediaImage(movie?.facts?.backdrop, 'w780');
    const dateLabel = date.toLocaleDateString([], { month: 'long', day: 'numeric', year: 'numeric' });
    const safeMovie = movie || { title: play.title, year: play.year, watched: play.completed, facts: play.facts };
    card.innerHTML = `<div class="timeline-date"><span class="timeline-dot"></span><time datetime="${date.toISOString()}">${escape(dateLabel)}</time><small>${escape(date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }))}</small></div><div class="timeline-card"><button class="timeline-open" aria-label="${escape(t(movie ? 'action.details' : 'drawer.matchConfirmation', { title: play.title, year: play.year || t('status.tba') }))}"><div class="timeline-art"><span class="timeline-art-fallback"><i class="fa-solid ${options.mediaType === 'tv' ? 'fa-tv' : 'fa-film'}" aria-hidden="true"></i></span>${backdrop ? `<img class="timeline-thumbnail" src="${backdrop}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}${poster ? `<img class="timeline-poster" src="${poster}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}</div><div class="timeline-copy"><span class="badge badge-subtle ${play.completed ? 'theme-primary' : 'theme-secondary'}"><i class="fa-solid ${play.completed ? 'fa-check-double' : 'fa-circle-half-stroke'}" aria-hidden="true"></i>${escape(t(options.mediaType === 'tv' ? 'tv.watched' : play.completed ? 'status.watched' : 'status.partial'))}</span><h3>${escape(play.title)}</h3>${options.mediaType === 'tv' ? `<p class="episode-title">${escape(play.episodeLabel)} · ${escape(play.episodeTitle)}</p>` : ''}<p class="movie-meta">${escape(play.year || t('status.tba'))}${movie?.facts?.runtime ? ` · ${escape(t('status.minutes', { count: movie.facts.runtime }))}` : ''}</p><p class="movie-genres">${escape((movie?.facts?.genres || []).slice(0, 2).join(' · '))}</p>${play.percent != null ? `<p class="field-help">${escape(t('status.progress', { percent: Math.round(play.percent) }))}</p>` : ''}</div>${ratingStrip(safeMovie, 'timeline-rating-strip')}</button>${!movie ? `<div class="timeline-match"><span>${escape(t('status.unmatched'))}</span><button class="btn-text">${escape(t('action.match'))}<i class="fa-solid fa-arrow-right" aria-hidden="true"></i></button></div>` : ''}</div>`;
    card.querySelector('.timeline-open').addEventListener('click', () => movie ? openMovie(movie) : openMatch(play));
    card.querySelector('.timeline-match button')?.addEventListener('click', () => openMatch(play));
    if (removeHistory) {
      const remove = document.createElement('button'); remove.className = 'btn-text timeline-remove';
      remove.textContent = 'Remove movie from history'; remove.addEventListener('click', () => removeHistory(play));
      card.querySelector('.timeline-card').append(remove);
    }
    rail.append(card);
  }
  $('#content').replaceChildren(rail); protectImages(rail); applyTimelinePreferences(); bindTimelineDrag(rail);
}

/** Applies timeline display preferences without rebuilding the timeline or resetting its scroll position. */
export function applyTimelinePreferences() {
  const rail = $('#timeline-rail'); if (!rail) return;
  rail.style.setProperty('--timeline-card', `${app.preferences.timelineZoom}px`);
  rail.classList.toggle('without-posters', !app.preferences.timelinePosters);
  rail.classList.toggle('without-thumbnails', !app.preferences.timelineThumbnails);
  rail.classList.toggle('without-ratings', !app.preferences.timelineRatings);
}

/** Adds mouse dragging and arrow-key navigation while preserving touch scrolling and normal card clicks. */
export function bindTimelineDrag(rail) {
  let start = null; let dragged = false;
  rail.addEventListener('pointerdown', (event) => {
    if (event.pointerType !== 'mouse' || event.button !== 0) return;
    start = { x: event.clientX, scroll: rail.scrollLeft, pointer: event.pointerId }; dragged = false;
  });
  rail.addEventListener('pointermove', (event) => {
    if (!start || event.buttons !== 1) return;
    const delta = event.clientX - start.x;
    if (Math.abs(delta) > 6) { dragged = true; rail.setPointerCapture(start.pointer); rail.classList.add('dragging'); }
    if (dragged) { rail.scrollLeft = start.scroll - delta; event.preventDefault(); }
  });
  const end = () => { start = null; rail.classList.remove('dragging'); };
  rail.addEventListener('pointerup', end); rail.addEventListener('pointercancel', end);
  rail.addEventListener('click', (event) => { if (dragged) { event.preventDefault(); event.stopImmediatePropagation(); dragged = false; } }, true);
  rail.addEventListener('dragstart', (event) => event.preventDefault());
  rail.addEventListener('keydown', (event) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault(); rail.scrollTo({ left: event.key === 'Home' ? 0 : event.key === 'End' ? rail.scrollWidth
      : rail.scrollLeft + (event.key === 'ArrowRight' ? 1 : -1) * (Number(app.preferences.timelineZoom) + 24), behavior: 'smooth' });
  });
  rail.addEventListener('wheel', (event) => {
    if (!event.shiftKey || event.ctrlKey || Math.abs(event.deltaX) >= Math.abs(event.deltaY) || rail.scrollWidth <= rail.clientWidth) return;
    const canMove = event.deltaY > 0 ? rail.scrollLeft < rail.scrollWidth - rail.clientWidth - 1 : rail.scrollLeft > 0;
    if (canMove) { event.preventDefault(); rail.scrollLeft += event.deltaY; }
  }, { passive: false });
}

/** Renders rich movie artwork, source ratings, credits, and safe external links inside the Bootstrap right drawer. */
export function renderDetails(movie, options = {}) {
  const facts = movie.facts || {}; const poster = mediaImage(movie.poster); const backdrop = mediaImage(facts.backdrop, 'w1280');
  const logo = imageUrl(facts.logo); const detail = options.container || $('#movie-details');
  const data = [[t('drawer.released'), movie.releaseDate?.slice(0, 10) || movie.year], [t('drawer.runtime'), facts.runtime ? t('status.minutes', { count: facts.runtime }) : null],
    [t('drawer.certification'), facts.certification], [options.mediaType === 'tv' && !options.episode ? 'Created by' : t('drawer.director'), facts.director], [t('drawer.language'), facts.language],
    [t('drawer.countries'), (facts.countries || []).join(', ')], [t('drawer.studios'), (facts.studios || []).join(', ')], ...(options.extraFacts || [])];
  detail.innerHTML = `<div class="movie-hero">${backdrop ? `<img class="movie-backdrop" src="${backdrop}" alt="" referrerpolicy="no-referrer">` : ''}<div class="movie-hero-shade"></div>${logo ? `<img class="movie-logo" src="${logo}" alt="${escape(movie.title)}" referrerpolicy="no-referrer">` : ''}</div><div class="movie-detail-main"><div class="movie-heading">${poster ? `<img class="detail-poster" src="${poster}" alt="" referrerpolicy="no-referrer">` : ''}<div><p class="eyebrow">${escape(movie.year || t('status.tba'))}${facts.runtime ? ` · ${escape(t('status.minutes', { count: facts.runtime }))}` : ''}</p><h2 id="${options.mediaType === 'tv' ? 'tv-title' : 'movie-title'}">${escape(movie.title)}</h2>${options.subtitle ? `<p class="episode-title">${escape(options.subtitle)}</p>` : ''}<p class="tagline">${escape(facts.tagline)}</p><div class="genre-tags">${(facts.genres || []).map((genre) => `<span class="badge badge-outline">${escape(genre)}</span>`).join('')}</div></div></div>${ratingStrip(movie, 'detail-ratings')}<p class="field-help rating-origin">${escape(t('status.ratingsOrigin', { source: facts.ratingOrigin || 'TMDB' }))}${facts.votes ? ` ${escape(t('status.votes', { count: facts.votes.toLocaleString() }))}` : ''}</p><section class="detail-section"><h3>${escape(t('drawer.synopsis'))}</h3><p class="dialog-overview">${escape(movie.overview || t('status.noSynopsis'))}</p></section><dl class="movie-facts">${data.filter(([, value]) => value).map(([label, value]) => `<div><dt>${escape(label)}</dt><dd>${escape(value)}</dd></div>`).join('')}</dl><div class="movie-links">${facts.trailer && /^https:\/\/www\.youtube\.com\/watch\?v=[a-zA-Z0-9_-]{6,40}$/.test(facts.trailer) ? `<a class="btn-outline" href="${escape(facts.trailer)}" target="_blank" rel="noopener noreferrer"><i class="fa-solid fa-play" aria-hidden="true"></i>${escape(t('drawer.trailer'))}</a>` : ''}${movie.tmdbId > 0 ? `<a class="btn-text" href="https://www.themoviedb.org/${options.mediaType === 'tv' ? 'tv' : 'movie'}/${movie.tmdbId}${options.episode ? `/season/${options.episode.season}/episode/${options.episode.episode}` : ''}" target="_blank" rel="noopener noreferrer">${escape(t('drawer.tmdb'))}<i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i></a>` : ''}${/^tt\d+$/.test(facts.imdbId || '') ? `<a class="btn-text" href="https://www.imdb.com/title/${facts.imdbId}/" target="_blank" rel="noopener noreferrer">${escape(t('drawer.imdb'))}</a>` : ''}</div><section class="detail-section"><h3>${escape(t('drawer.cast'))}</h3><div class="cast-grid">${(facts.cast || []).map((actor) => `<div class="cast-card">${imageUrl(actor.photo, 'w185') ? `<img src="${imageUrl(actor.photo, 'w185')}" alt="" loading="lazy" referrerpolicy="no-referrer">` : '<span class="cast-placeholder"><i class="fa-solid fa-user" aria-hidden="true"></i></span>'}<strong>${escape(actor.name)}</strong><small>${escape(actor.character)}</small></div>`).join('') || `<p class="field-help">${escape(t('status.noCast'))}</p>`}</div></section></div>`;
  protectImages(detail);
}

/** Hides failed artwork so browser error glyphs do not replace the designed image fallback. */
export function protectImages(root) {
  root.querySelectorAll('img').forEach((image) => image.addEventListener('error', () => { image.hidden = true; }, { once: true }));
}
