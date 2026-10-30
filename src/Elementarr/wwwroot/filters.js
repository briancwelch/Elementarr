import { $, app, escape } from './core.js';
import { t, ui } from './ui.js';
import { bindChipFilters, chipChoice, chipField, commitChipFilters, internationalChoices, writeChipFilters } from './filter-chips.js';

Object.assign(ui.words, {
  'filter.release': 'Release year & decade', 'filter.fromYear': 'From year', 'filter.toYear': 'To year',
  'filter.ratings': 'Review scores', 'filter.unrated': 'Include movies with missing scores', 'filter.minimumVotes': 'Minimum TMDB votes',
  'filter.genres': 'Genres', 'filter.excludeGenres': 'Exclude genres', 'filter.genreMode': 'Genre matching',
  'filter.runtime': 'Runtime', 'filter.minutesFrom': 'Minimum minutes', 'filter.minutesTo': 'Maximum minutes',
  'filter.runtimeRange': '{min}–{max} min',
  'filter.languageCountry': 'Language, country & certification', 'filter.language': 'Original languages',
  'filter.country': 'Production countries', 'filter.certification': 'Content certifications (ratings)',
  'filter.states': 'Your movie collection', 'filter.owned': 'Managed by Radarr', 'filter.available': 'File available',
  'filter.watched': 'Watched movie', 'filter.history': 'History & dates', 'filter.completion': 'Play completion',
  'filter.matched': 'Matched to TMDB', 'filter.watchFrom': 'Watched from', 'filter.watchTo': 'Watched through',
  'filter.releaseFrom': 'Released from', 'filter.releaseTo': 'Released through', 'filter.completed': 'Completed plays',
  'filter.partial': 'Partial plays', 'filter.noDecade': 'Any decade',
  'filter.chooseValues': 'Choose or type to search…', 'filter.chipHelp': 'Choose a suggestion or type a genre, then press Enter.',
  'filter.chipCodeHelp': 'Choose a suggestion or enter a language/country code, then press Enter.',
  'filter.chipRatingHelp': 'Choose a rating or enter a regional certification, then press Enter.',
  'filter.chooseKnownValue': 'Choose an available value or enter a valid code.', 'filter.removeValue': 'Remove {value}',
  'filter.voteCount': '{count} votes'
});

const ratingRanges = [['imdb', 'source.imdb', 10, .1], ['rt', 'source.rtLong', 100, 1], ['tmdb', 'source.tmdb', 10, .1]];
const runtimeMaximum = 1000;
const voteThresholds = [0, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000, 100000, 250000, 500000, 1000000, 10000000, 100000000];

/** Creates a labeled filter field using plain text defaults and a fixed input type. */
function field(name, label, type = 'number', attributes = '') {
  return `<label class="filter-field"><span>${escape(t(label))}</span><input class="form-control" name="${name}" type="${type}" ${attributes}></label>`;
}

/** Builds a tri-state status selector. */
function state(name, label) {
  return `<label class="filter-field"><span>${escape(t(label))}</span><select class="form-control" name="${name}">${['any', 'yes', 'no'].map((value) => `<option value="${value}">${escape(t('filter.' + value))}</option>`).join('')}</select></label>`;
}

/** Builds the extensive filter drawer with independent native-scale rating ranges. */
export function buildFilters(genres) {
  const television = app.view.startsWith('tv-');
  const labels = television ? { 'filter.unrated': 'Include shows with missing scores', 'filter.states': 'Your TV collection',
    'filter.owned': 'Managed by Sonarr', 'filter.watched': 'Has watched episodes', 'filter.runtime': 'Episode runtime',
    'filter.completion': 'Show completion', 'filter.completed': 'Completed shows', 'filter.partial': 'Shows in progress' } : {};
  const label = (key) => labels[key] || t(key);
  const group = (title, content, open = false) => `<details class="filter-group" ${open ? 'open' : ''}><summary>${escape(label(title))}</summary><div class="filter-group-body">${content}</div></details>`;
  const decades = Array.from({ length: 15 }, (_, index) => 1880 + index * 10);
  const years = `<div class="decade-chips"><button type="button" class="badge badge-outline" data-decade="">${escape(t('filter.noDecade'))}</button>${decades.map((year) => `<button type="button" class="badge badge-outline" data-decade="${year}">${year}s</button>`).join('')}</div><div class="field-pair">${field('yearFrom', 'filter.fromYear', 'number', 'min="1870" max="2200" placeholder="1870"')}${field('yearTo', 'filter.toYear', 'number', 'min="1870" max="2200" placeholder="2200"')}</div>`;
  const ratings = ratingRanges.map(([source, label, max, step]) => `<fieldset class="rating-range"><legend><span class="rating-source ${source}">${escape(t(label))}</span><output data-range-output="${source}">0 – ${max}</output></legend><div class="range-pair"><label><span class="visually-hidden">${escape(t('filter.min'))}</span><input type="range" name="${source}Min" min="0" max="${max}" step="${step}" value="0" aria-label="${escape(t(label) + ' ' + t('filter.min'))}"></label><label><span class="visually-hidden">${escape(t('filter.max'))}</span><input type="range" name="${source}Max" min="0" max="${max}" step="${step}" value="${max}" aria-label="${escape(t(label) + ' ' + t('filter.max'))}"></label></div></fieldset>`).join('')
    + `<label class="toggle-label"><input name="includeUnrated" type="checkbox" class="check"><span>${escape(label('filter.unrated'))}</span></label><fieldset class="rating-range votes-range"><legend><label for="filter-votesMin">${escape(t('filter.minimumVotes'))}</label><output for="filter-votesMin"></output></legend><input id="filter-votesMin" type="range" name="votesMin" min="0" max="${voteThresholds.length - 1}" step="1" value="0"></fieldset>`;
  const genreFilter = chipField('genres', 'filter.genres', genres, 'success') + `<label class="filter-field"><span>${escape(t('filter.genreMode'))}</span><select name="genreMode" class="form-control"><option value="any">${escape(t('filter.anyGenre'))}</option><option value="all">${escape(t('filter.allGenres'))}</option></select></label>` + chipField('excludeGenres', 'filter.excludeGenres', genres, 'danger');
  const choices = internationalChoices(television);
  const international = chipField('language', 'filter.language', choices.language) + chipField('country', 'filter.country', choices.country)
    + chipField('certification', 'filter.certification', choices.certification);
  const history = `<label class="filter-field"><span>${escape(label('filter.completion'))}</span><select name="completion" class="form-control"><option value="any">${escape(t('filter.any'))}</option><option value="completed">${escape(label('filter.completed'))}</option><option value="partial">${escape(label('filter.partial'))}</option></select></label>${state('matched', 'filter.matched')}<div class="field-pair">${field('watchedFrom', 'filter.watchFrom', 'date')}${field('watchedTo', 'filter.watchTo', 'date')}</div><div class="field-pair">${field('releasedFrom', 'filter.releaseFrom', 'date')}${field('releasedTo', 'filter.releaseTo', 'date')}</div>`;
  const runtime = `<fieldset class="rating-range"><legend><span>${escape(label('filter.runtime'))}</span><output>${escape(t('filter.runtimeRange', { min: 0, max: runtimeMaximum }))}</output></legend><div class="range-pair">${[['Min', 'filter.minutesFrom', 0], ['Max', 'filter.minutesTo', runtimeMaximum]].map(([bound, label, value]) => `<label><span>${escape(t(label))}</span><input type="range" name="runtime${bound}" min="0" max="${runtimeMaximum}" step="1" value="${value}" aria-label="${escape(t(label))}"></label>`).join('')}</div></fieldset>`;
  $('#filter-fields').innerHTML = group('filter.release', years, true) + group('filter.ratings', ratings, true)
    + group('filter.genres', genreFilter) + group('filter.runtime', runtime)
    + group('filter.languageCountry', international) + group('filter.states', state('owned', 'filter.owned') + state('available', 'filter.available') + state('watched', 'filter.watched'))
    + group('filter.history', history) + `<p class="field-help">${escape(t('filter.knownData'))}</p><p class="field-help">${escape(t('filter.indexHelp'))}</p>`;
  if (television) {
    for (const [name, key] of [['owned', 'filter.owned'], ['watched', 'filter.watched']]) $('#filter-form').elements[name].closest('label').querySelector('span').textContent = label(key);
    $('#filter-fields').querySelectorAll('p.field-help').forEach((p, index) => { p.textContent = index === 0 ? 'Filters use known TV metadata and your episode history.' : 'Refresh TV metadata in Connections to fill missing fields.'; });
  }
  $('#filter-fields').querySelectorAll('[data-decade]').forEach((button) => button.addEventListener('click', () => {
    const decade = Number(button.dataset.decade); $('#filter-form').elements.yearFrom.value = decade || '';
    $('#filter-form').elements.yearTo.value = decade ? decade + 9 : '';
    $('#filter-fields').querySelectorAll('[data-decade]').forEach((chip) => chip.classList.toggle('selected', chip === button));
  }));
  $('#filter-fields').querySelectorAll('input[type="range"]').forEach((input) => input.addEventListener('input', () => updateRange(input)));
  bindChipFilters();
  writeFilters();
}

/** Shows vote thresholds and keeps paired rating/runtime bounds ordered on their native scales. */
function updateRange(input) {
  const fieldset = input.closest('fieldset');
  if (input.name === 'votesMin') {
    const count = voteValue(input).toLocaleString();
    fieldset.querySelector('output').textContent = t('filter.voteCount', { count });
    input.setAttribute('aria-valuetext', t('filter.voteCount', { count })); return;
  }
  const [min, max] = fieldset.querySelectorAll('input');
  if (Number(min.value) > Number(max.value)) { if (input === min) max.value = min.value; else min.value = max.value; }
  fieldset.querySelector('output').textContent = t(min.name === 'runtimeMin' ? 'filter.runtimeRange' : 'status.range', { min: min.value, max: max.value });
}

/** Maps slider positions to useful vote thresholds, including an exact previously saved threshold. */
function voteValue(input) {
  return (input.voteThresholds || voteThresholds)[Number(input.value)];
}

/** Copies active filters into the drawer without firing remote requests while a slider is being adjusted. */
export function writeFilters() {
  const form = $('#filter-form'); form.reset();
  for (const input of form.elements) {
    if (!input.name) continue;
    const value = app.filters[input.name];
    if (input.type === 'hidden') input.value = value || '';
    else if (input.type === 'checkbox') input.checked = Boolean(value);
    else if (input.name === 'votesMin') {
      const threshold = Math.min(100000000, Math.max(0, Number(value) || 0));
      input.voteThresholds = [...new Set([...voteThresholds, threshold])].sort((a, b) => a - b);
      input.max = input.voteThresholds.length - 1; input.value = input.voteThresholds.indexOf(threshold);
    } else if (value != null) input.value = value;
  }
  writeChipFilters();
  form.querySelectorAll('input[type="range"]').forEach(updateRange);
  updateFilterSummary();
}

/** Reads filters into query-safe scalars while omitting unrestricted rating and runtime defaults. */
export function readFilters() {
  if (!commitChipFilters()) return null;
  const form = $('#filter-form'); const values = new FormData(form); const next = {};
  for (const [key, value] of values) {
    if (!String(value).trim() || ['genres', 'excludeGenres'].includes(key) || value === 'any') continue;
    if (key === 'includeUnrated') next[key] = true;
    else next[key] = String(value).trim();
  }
  next.genres = values.getAll('genres').join(','); next.excludeGenres = values.getAll('excludeGenres').join(',');
  for (const [source, , max] of [...ratingRanges, ['runtime', 'filter.runtime', runtimeMaximum]]) {
    if (Number(next[source + 'Min']) === 0) delete next[source + 'Min'];
    if (Number(next[source + 'Max']) === max) delete next[source + 'Max'];
  }
  if (next.country) next.country = next.country.toUpperCase();
  if (next.language) next.language = next.language.toLowerCase();
  const votes = voteValue(form.elements.votesMin);
  if (votes > 0) next.votesMin = String(votes); else delete next.votesMin;
  return { ...next, sort: $('#sort-by').value, order: $('#sort-order').value };
}

/** Shows the count and removable chips for active filters, excluding display and sorting preferences. */
export function updateFilterSummary() {
  const entries = Object.entries(app.filters).filter(([key, value]) => value !== '' && value != null && !['sort', 'order', 'genreMode'].includes(key) && value !== 'any' && value !== false);
  $('#filter-count').textContent = t('filter.selected', { count: entries.length }); $('#filter-count').hidden = entries.length === 0;
  $('#open-filters').classList.toggle('has-filters', entries.length > 0);
  $('#active-filters').replaceChildren();
  for (const [key, value] of entries) {
    const multiple = ['genres', 'excludeGenres', 'language', 'country', 'certification'].includes(key);
    for (const selected of multiple ? String(value).split(',').filter(Boolean) : [value]) {
      const choice = chipChoice(key, selected), text = `${filterLabel(key)}: ${choice?.label || (typeof selected === 'boolean' ? t('filter.yes') : selected)}`;
      const chip = document.createElement('span'); chip.className = `chip theme-${key === 'genres' ? 'success' : key === 'excludeGenres' ? 'danger' : 'primary'}`;
      chip.innerHTML = `${choice?.icon ? `<i class="chip-icon fa-solid fa-${choice.icon}" aria-hidden="true"></i>` : ''}${escape(text)}<button type="button" class="chip-dismiss" aria-label="${escape(t('filter.removeValue', { value: text }))}"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>`;
      chip.querySelector('button').addEventListener('click', () => {
        const remaining = multiple ? String(app.filters[key] || '').split(',').filter(item => item !== selected).join(',') : '';
        if (remaining) app.filters[key] = remaining; else delete app.filters[key];
        writeFilters(); document.dispatchEvent(new Event('elementarr:filters-applied'));
      });
      $('#active-filters').append(chip);
    }
  }
}

/** Resolves the human label for a filter chip from its form label or rating fieldset. */
function filterLabel(key) {
  if (key === 'votesMin') return t('filter.minimumVotes');
  const element = $('#filter-form').elements.namedItem(key);
  const input = element instanceof RadioNodeList ? element[0] : element;
  const rating = input?.closest('fieldset')?.querySelector('legend span')?.textContent;
  return input?.closest('.filter-field')?.querySelector('label, span')?.textContent
    || (rating ? rating + ' ' + t(key.endsWith('Min') ? 'filter.min' : 'filter.max') : null)
    || (key === 'genres' ? t('filter.genres') : key === 'includeUnrated' ? t('filter.unrated') : key);
}

/** Builds sorting choices shared by poster lists and the history timeline. */
export function buildSortOptions() {
  const selected = app.filters.sort || (['history', 'tv-history', 'tv-issues'].includes(app.view) ? 'watched' : ['library', 'tv-library'].includes(app.view) ? 'title' : 'default');
  $('#sort-by').replaceChildren(...['default', 'title', 'year', 'tmdb', 'imdb', 'rt', 'runtime', 'votes', 'popularity', ...(['history', 'issues', 'tv-history', 'tv-issues'].includes(app.view) ? ['watched'] : [])]
    .map((key) => new Option(t('sort.' + key), key)));
  $('#sort-by').value = selected; $('#sort-order').value = app.filters.order || (['library', 'tv-library'].includes(app.view) ? 'asc' : 'desc');
}
