export const ui = { settings: { texts: {} }, defaults: {}, words: {} };

// Dynamic wording is registered here; static markup registers its data-text attributes at startup.
Object.assign(ui.words, {
  'view.for-you.eyebrow': 'MADE FOR YOUR TASTE', 'view.for-you.title': 'Find your next favorite.',
  'view.for-you.description': 'A little less scrolling. A lot more good movies.', 'view.for-you.caption': 'Picked for you',
  'view.trending.eyebrow': 'THE MOVIES EVERYONE IS TALKING ABOUT', 'view.trending.title': 'Worth the conversation.',
  'view.trending.description': 'Explore the films trending this week.', 'view.trending.caption': 'Trending this week',
  'view.upcoming.eyebrow': 'SOMETHING TO LOOK FORWARD TO', 'view.upcoming.title': 'Your future movie nights.',
  'view.upcoming.description': 'Upcoming movies over the next twelve months. Release dates can change.', 'view.upcoming.caption': 'On the horizon',
  'view.top-rated.eyebrow': 'GREAT FILMS, GREAT COMPANY', 'view.top-rated.title': 'Make time for a classic.',
  'view.top-rated.description': 'Highly rated films with at least 300 TMDB votes.', 'view.top-rated.caption': 'Top rated movies',
  'view.library.eyebrow': 'YOUR VERY OWN FILM UNIVERSE', 'view.library.title': 'The collection.',
  'view.library.description': 'Your Radarr movies, with availability and ratings at a glance.', 'view.library.caption': 'Your movies',
  'view.history.eyebrow': 'ONE GOOD MOVIE AFTER ANOTHER', 'view.history.title': 'Your story, in movies.',
  'view.history.description': 'A timeline of your personal movie history.', 'view.history.caption': 'Watched Movie History',
  'view.issues.eyebrow': 'BRING EVERY MOVIE INTO FOCUS', 'view.issues.title': 'Resolve your history.',
  'view.issues.description': 'Match an affected item to TMDB. Your correction applies to its repeat plays and future syncs.', 'view.issues.caption': 'Affected history items',
  'view.settings.eyebrow': 'YOUR SERVER. YOUR MOVIES.', 'view.settings.title': 'Bring it all together.',
  'view.settings.description': 'Connect your movie and TV services and choose where new discoveries go.', 'view.settings.caption': 'Connections',
  'view.admin.eyebrow': 'MAKE THIS SPACE YOURS', 'view.admin.title': 'The control room.',
  'view.admin.description': 'Appearance, wording, metadata, automatic updates, and server preferences.', 'view.admin.caption': 'Administration',
  'error.signIn': 'Please sign in again.', 'error.rateLimit': 'Too many attempts. Wait a minute and try again.',
  'error.password': 'That password did not match.', 'error.request': 'The request failed ({status}).',
  'error.start': 'Cannot load Elementarr: {message}', 'error.form': 'Please complete the highlighted fields.',
  'error.plexAddress': 'Plex returned an unexpected authorization address.', 'error.plexExpired': 'Plex authorization expired. Please try again.',
  'action.dismiss': 'Dismiss notification', 'action.close': 'Close', 'action.retry': 'Try again',
  'action.add': 'Add to Radarr', 'action.searchNow': 'Search for Download Immediately', 'action.inRadarr': 'In Radarr',
  'action.match': 'Find the correct movie', 'action.saveMatch': 'Use this movie', 'action.keepSearching': 'Continue finding matches',
  'action.previous': 'Previous', 'action.next': 'Next', 'action.viewAffected': 'View Affected Items',
  'action.details': 'Details for {title}', 'action.timelineBack': 'Scroll timeline backward', 'action.timelineForward': 'Scroll timeline forward',
  'action.collapse': 'Collapse navigation', 'action.expand': 'Expand navigation',
  'status.available': 'Available in your library', 'status.watched': 'Watched', 'status.partial': 'Partial play',
  'status.managed': 'Managed by Radarr', 'status.unmatched': 'Needs a movie match', 'status.unrated': 'Rating unavailable',
  'status.loading': 'Finding your movies…', 'status.fetching': 'Loading artwork, ratings, and movie data.',
  'status.noResults': 'No matching movies.', 'status.noResultsHelp': 'Adjust your filters or sync your sources to bring movies into this view.',
  'status.errorTitle': 'The movies need a moment.', 'status.noTmdb': 'Connect TMDB to start exploring.',
  'status.noTmdbHelp': 'Add your TMDB Read Access Token in Connections. Local collection and history remain available.',
  'status.notSynced': 'not synced', 'status.freshness': 'Collection: {collection} · History: {history}',
  'status.unmatchedWarning': '{count} history entries need a reliable TMDB match. Watched filtering may miss these movies.',
  'status.syncing': 'Syncing configured sources…', 'status.synced': '{sources} synced.',
  'status.reviewNotices': 'Review the source notices above.', 'status.collectionSource': 'Radarr collection',
  'status.historySource': '{source} history', 'status.and': ' and ',
  'status.searchSent': 'Search sent to Radarr for {title} · {status}.', 'status.searchFailed': 'Radarr search failed for {title}: {message}',
  'status.added': '{title} added to Radarr.', 'status.matched': 'Saved the movie match for {count} history entries.',
  'status.searchRunning': 'Radarr search for {title}: {status}.', 'status.commandUnknown': 'The search was sent, but its latest status could not be retrieved.',
  'status.command.queued': 'queued', 'status.command.started': 'running', 'status.command.completed': 'completed',
  'status.command.failed': 'failed', 'status.command.aborted': 'aborted', 'status.command.unknown': 'unknown',
  'status.page': 'Page {page} of {pages} · {count} results', 'status.pageMore': 'Page {page} · {count} matches found · more to explore',
  'status.range': '{min} – {max}', 'status.tba': 'TBA', 'status.na': '—', 'status.minutes': '{count} min',
  'status.votes': '{count} TMDB votes', 'status.watchDate': 'Watched {date}', 'status.progress': '{percent}% played',
  'status.matchNeeded': 'This entry has no TMDB match. Find the correct movie to unlock its artwork and watched filtering.',
  'status.noSynopsis': 'No synopsis is available yet.', 'status.noCast': 'Cast information is not available.',
  'status.noTrailer': 'No trailer is available.', 'status.ratingsOrigin': 'Ratings supplied by {source}. Missing scores are not estimated.',
  'status.metadataPartial': 'Some metadata is unavailable. Refresh movie metadata in Administration to build the local index.',
  'status.remoteSearch': 'More source results remain. Filters search subsequent results as you continue; sorting applies to the movies loaded so far.',
  'status.plexLinked': 'Linked to {name}. Plex is your primary history source and enables Plex sign-in.',
  'status.plexUnlinked': 'Connect your Plex account. Your Plex password is entered only on the Plex sign-in page.',
  'status.plexConnected': 'Plex connected. You can now sync history and use Sign in with Plex.', 'status.plexSignedIn': 'Signed in with Plex.',
  'status.plexWaiting': 'Waiting for Plex authorization…', 'status.savedSecret': 'Saved · leave blank to keep',
  'status.savedProfile': 'Saved profile #{id}', 'status.resolveUser': '{name} · resolve on first sync',
  'status.loadProfiles': 'Check connections to load profiles', 'status.loadFolders': 'Check connections to load folders',
  'status.chooseUser': 'Select your Tautulli user', 'status.connected': 'Connected', 'status.attention': 'Needs attention',
  'status.disabled': 'Disabled', 'status.notConfigured': 'Not configured', 'status.connectionsSaved': 'Connections saved.',
  'status.connectionsChecked': 'Configured sources checked. Save your Radarr destination, then sync collection or history independently.',
  'drawer.movieNight': 'YOUR NEXT MOVIE NIGHT', 'drawer.synopsis': 'The story', 'drawer.cast': 'Cast',
  'drawer.director': 'Director', 'drawer.genres': 'Genres', 'drawer.studios': 'Studios', 'drawer.countries': 'Production countries',
  'drawer.language': 'Original language', 'drawer.released': 'Release date', 'drawer.runtime': 'Runtime', 'drawer.certification': 'Certification',
  'drawer.trailer': 'Watch trailer', 'drawer.imdb': 'View on IMDb', 'drawer.tmdb': 'View on TMDB',
  'drawer.ratings': 'Ratings', 'drawer.destination': 'Monitored · {folder} · quality profile #{profile}',
  'drawer.noDestination': 'Choose a Radarr quality profile and root folder in Connections before adding a movie.',
  'drawer.searchHelp': 'Search Immediately adds the movie to Radarr if needed, then queues a download search. A queued search does not guarantee a download.',
  'drawer.matchTitle': 'Match this history item', 'drawer.matchHelp': 'Search by movie title, review the year and poster, then choose the correct movie. You can also correct metadata in Plex and sync again.',
  'drawer.matchSearch': 'Search movie titles', 'drawer.matchYear': 'Original entry: {title} ({year})',
  'drawer.matchConfirmation': 'Match to {title} ({year})', 'drawer.noMatchResults': 'No movie matches. Try another title.',
  'source.imdb': 'IMDb', 'source.rt': 'RT', 'source.rtLong': 'Rotten Tomatoes', 'source.tmdb': 'TMDB',
  'filter.any': 'Any', 'filter.yes': 'Yes', 'filter.no': 'No', 'filter.min': 'Minimum', 'filter.max': 'Maximum',
  'filter.allGenres': 'Match all selected genres', 'filter.anyGenre': 'Match any selected genre',
  'filter.selected': '{count} filters active', 'filter.noActive': 'All movies',
  'filter.knownData': 'Filters use known metadata. Unknown scores are excluded from active rating ranges unless you include unrated movies.',
  'filter.indexHelp': 'Missing genres, countries, and certifications can be filled by a metadata refresh in Administration.',
  'sort.default': 'Feed order', 'sort.title': 'Title', 'sort.year': 'Release year', 'sort.tmdb': 'TMDB rating',
  'sort.imdb': 'IMDb rating', 'sort.rt': 'Rotten Tomatoes',
  'sort.runtime': 'Runtime', 'sort.votes': 'TMDB vote count', 'sort.popularity': 'Popularity', 'sort.watched': 'Watch date',
  'sort.asc': 'Ascending', 'sort.desc': 'Descending',
  'admin.canvas': 'Page background', 'admin.panel': 'Panel background', 'admin.accent': 'Accent color',
  'admin.ink': 'Text color', 'admin.muted': 'Secondary text', 'admin.border': 'Border color',
  'admin.bodyFont': 'Body font family', 'admin.headingFont': 'Heading font family', 'admin.googleFonts': 'Load selected families from Google Fonts',
  'admin.fontSize': 'Base font size', 'admin.headingSize': 'Heading size', 'admin.navWidth': 'Navigation width',
  'admin.navigationFontScale': 'Navigation text', 'admin.cardFontScale': 'Cards and timelines',
  'admin.detailFontScale': 'Details and dialogs', 'admin.controlFontScale': 'Forms and controls',
  'admin.captionFontScale': 'Captions and help text', 'admin.badgeFontScale': 'Badge text', 'admin.toastFontScale': 'Notification text',
  'admin.badgeRadius': 'Badge corner radius', 'admin.toastDuration': 'Notification duration',
  'admin.colorsTitle': 'Colors', 'admin.typographyTitle': 'Typography', 'admin.layoutTitle': 'Layout and notifications',
  'admin.typographyHelp': 'Base font size scales the whole interface. Category controls adjust text relative to that base; heading size is measured at the default 14 px base.',
  'view.changelog.eyebrow': 'WHAT IS NEW', 'view.changelog.title': 'Elementarr, evolving.',
  'view.changelog.caption': 'Changelog', 'view.changelog.description': 'Release notes included with this installation. Updates preserve your settings and watch history.',
  'admin.contentWidth': 'Maximum content width', 'admin.cornerRadius': 'Corner radius', 'admin.gridGap': 'Grid spacing',
  'admin.posterSize': 'Default poster width', 'admin.pageSize': 'Default items per page', 'admin.timelineZoom': 'Default timeline zoom',
  'admin.showPosters': 'Show timeline posters', 'admin.showBackdrops': 'Show timeline thumbnails', 'admin.showRatings': 'Show poster ratings',
  'admin.showReasons': 'Show recommendation reasons', 'admin.showFooter': 'Show footer', 'admin.animations': 'Enable animations',
  'admin.saved': 'Appearance and wording saved.', 'admin.previewed': 'Preview applied locally. Save to use these settings on every device.',
  'admin.resetPreview': 'Default appearance previewed. Save to keep it.', 'admin.defaultText': 'Default', 'admin.overrideText': 'Your wording',
  'admin.textCount': '{count} wording options', 'admin.noWords': 'No wording matches your search.',
  'admin.imported': 'Imported ratings for {count} movies.', 'admin.invalidImport': 'Select a JSON file with a movies array.',
  'admin.importHelp': 'Import up to 100 movie scores per file using TMDB IDs. IMDb/TMDB use 0–10 and Rotten Tomatoes 0–100. Imported scores are labeled as a local import.',
  'admin.indexStatus': '{done} / {total} movies indexed · {failed} unavailable', 'admin.indexIdle': 'Metadata refresh is idle.',
  'admin.indexRunning': 'Refreshing metadata…', 'admin.indexStarted': 'Metadata refresh started. You can continue browsing.',
  'admin.indexHelp': 'Fetch detailed movie metadata from TMDB for your collection and matched history. This can take several minutes. Cached work survives restarts.',
  'admin.noCustomProvider': 'Ratings come from Radarr, Plex, TMDB, and optional local imports.'
});

/** Registers static defaults under stable keys so every label, help string, and placeholder appears in Administration. */
export function registerTexts(root = document) {
  root.querySelectorAll('[data-text]').forEach((element) => { ui.words[element.dataset.text] ??= element.textContent.trim(); });
  for (const [attribute, dataset] of [['placeholder', 'placeholderKey'], ['aria-label', 'labelKey'], ['title', 'titleKey']])
    root.querySelectorAll(`[data-${dataset.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase())}]`).forEach((element) => {
      ui.words[element.dataset[dataset]] ??= element.getAttribute(attribute) || '';
    });
}

/** Expands only plain-text template values; callers escape the result before generating HTML. */
export function t(key, values = {}) {
  const template = Object.hasOwn(ui.settings.texts || {}, key) ? ui.settings.texts[key] : ui.words[key] ?? key;
  return String(template).replace(/\{(\w+)\}/g, (match, name) => Object.hasOwn(values, name) ? String(values[name]) : match);
}

/** Applies configurable text using safe DOM properties rather than HTML interpretation. */
export function applyTexts(root = document) {
  root.querySelectorAll('[data-text]').forEach((element) => { element.textContent = t(element.dataset.text); });
  for (const [attribute, dataset] of [['placeholder', 'placeholderKey'], ['aria-label', 'labelKey'], ['title', 'titleKey']])
    root.querySelectorAll(`[data-${dataset.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase())}]`).forEach((element) => element.setAttribute(attribute, t(element.dataset[dataset])));
}

/** Loads public branding for both the password screen and authenticated UI without exposing service credentials. */
export async function loadAppearance() {
  const [current, defaults] = await Promise.all([fetch('/api/appearance'), fetch('/api/appearance/defaults')]);
  if (!current.ok || !defaults.ok) throw new Error(t('error.request', { status: current.status }));
  ui.settings = await current.json(); ui.defaults = await defaults.json();
  applyAppearance(ui.settings); applyTexts();
}

/** Applies validated theme variables and optionally loads named Google Fonts from Google's fixed stylesheet endpoint. */
export function applyAppearance(settings) {
  const style = document.documentElement.style;
  for (const key of ['canvas', 'panel', 'accent', 'ink', 'muted', 'border'])
    if (/^#[\da-f]{6}$/i.test(settings[key])) style.setProperty(`--${key === 'border' ? 'line' : key}`, settings[key]);
  const numeric = { fontSize: 'base-size', navWidth: 'nav-expanded', contentWidth: 'content-width', cornerRadius: 'radius', badgeRadius: 'badge-radius', gridGap: 'grid-gap' };
  for (const [key, variable] of Object.entries(numeric)) if (Number.isFinite(Number(settings[key]))) style.setProperty(`--${variable}`, `${Number(settings[key])}px`);
  style.setProperty('--heading-size', `calc(1rem * ${Number(settings.headingSize || 36) / 14})`);
  style.setProperty('--text-ratio', String(Number(settings.fontSize || 14) / 14));
  for (const key of ['navigation', 'card', 'detail', 'control', 'caption', 'badge', 'toast'])
    style.setProperty(`--${key}-font-scale`, String(Number(settings[key + 'FontScale'] ?? 100) / 100));
  for (const [key, variable] of [['bodyFont', 'body-font'], ['headingFont', 'heading-font']])
    if (/^[a-zA-Z0-9][a-zA-Z0-9 -]{0,79}$/.test(settings[key])) style.setProperty(`--${variable}`, `"${settings[key]}", system-ui, sans-serif`);
  document.body.classList.toggle('no-animation', !settings.animations);
  document.body.classList.toggle('hide-ratings', !settings.showRatings);
  document.body.classList.toggle('hide-reasons', !settings.showReasons);
  document.body.classList.toggle('hide-footer', !settings.showFooter);
  document.querySelector('#google-fonts')?.remove();
  if (settings.googleFonts) {
    const system = new Set(['segoe ui', 'arial', 'georgia', 'verdana', 'tahoma', 'trebuchet ms', 'times new roman', 'courier new', 'system-ui']);
    const families = [...new Set([settings.bodyFont, settings.headingFont])].filter((name) => /^[a-zA-Z0-9][a-zA-Z0-9 -]{0,79}$/.test(name) && !system.has(name.toLowerCase()));
    if (!families.length) return;
    const link = document.createElement('link'); link.id = 'google-fonts'; link.rel = 'stylesheet';
    link.href = 'https://fonts.googleapis.com/css2?' + families.map((name) => 'family=' + encodeURIComponent(name).replace(/%20/g, '+')).join('&') + '&display=swap';
    document.head.append(link);
  }
}

/** Reads browser display preferences without making storage availability a requirement for using the app. */
export function readPreferences() {
  try { return JSON.parse(localStorage.getItem('elementarr.display.v2') || '{}'); } catch { return {}; }
}

/** Persists only non-sensitive display choices on the current browser. */
export function savePreferences(value) {
  try { localStorage.setItem('elementarr.display.v2', JSON.stringify(value)); } catch { /* Private or full storage does not block browsing. */ }
}

