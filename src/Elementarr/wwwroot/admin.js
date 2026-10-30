import { $, api, busy, escape, notify } from './core.js';
import { ui, t, applyAppearance, applyTexts } from './ui.js';

let wordDraft = {};
let wordSection = 'ui';
Object.assign(ui.words, { 'admin.indexUnavailable': 'Check the affected movie IDs in Radarr, open their TMDB pages, or retry the refresh. Existing movie data is retained.',
  'admin.metadataLookup': 'View movie on TMDB' });
let poll = 0;
const colors = ['canvas', 'panel', 'accent', 'ink', 'muted', 'border'];
const fonts = ['bodyFont', 'headingFont'];
const ranges = { fontSize: [11, 22], headingSize: [24, 60], navWidth: [180, 300], contentWidth: [1000, 3000],
  cornerRadius: [0, 24], badgeRadius: [0, 24], toastDuration: [1, 60], gridGap: [8, 40], posterSize: [120, 320], timelineZoom: [130, 360],
  navigationFontScale: [75, 175], cardFontScale: [75, 175], detailFontScale: [75, 175], controlFontScale: [75, 175],
  captionFontScale: [75, 175], badgeFontScale: [75, 175], toastFontScale: [75, 175] };
const toggles = ['googleFonts', 'showPosters', 'showBackdrops', 'showRatings', 'showReasons', 'showFooter', 'animations'];
const wordSections = {
  ui: ['brand', 'nav', 'search', 'stats', 'browse', 'heading', 'common', 'footer', 'sync', 'setup'],
  movies: ['movie', 'view', 'drawer', 'timeline', 'source', 'match'],
  filters: ['filter', 'filters', 'sort'],
  connections: ['connections', 'login', 'plex'],
  admin: ['admin'],
  other: []
};

/** Builds editable appearance controls from safe named settings rather than allowing arbitrary CSS or scripts. */
export function renderAdmin(settings = ui.settings) {
  wordDraft = { ...(settings.texts || {}) };
  const colorFields = colors.map((key) => `<label class="appearance-color"><span>${escape(t('admin.' + key))}</span><input type="color" name="${key}" value="${escape(settings[key])}"><code>${escape(settings[key])}</code></label>`).join('');
  const fontFields = fonts.map((key) => `<label class="appearance-field"><span>${escape(t('admin.' + key))}</span><input class="form-control" name="${key}" list="font-families" maxlength="80" pattern="[A-Za-z0-9][A-Za-z0-9 -]{0,79}" required value="${escape(settings[key])}"></label>`).join('');
  const rangeFields = (typography) => Object.entries(ranges).filter(([key]) => (key.endsWith('FontScale') || ['fontSize', 'headingSize'].includes(key)) === typography)
    .map(([key, [min, max]]) => `<label class="appearance-field"><span>${escape(t('admin.' + key))}</span><div class="range-value"><input type="range" name="${key}" min="${min}" max="${max}" value="${settings[key]}"><output>${settings[key]} ${rangeUnit(key)}</output></div></label>`).join('');
  $('#appearance-fields').innerHTML = `<fieldset class="appearance-category"><legend>${escape(t('admin.colorsTitle'))}</legend><div class="appearance-group-grid">${colorFields}</div></fieldset>`
    + `<fieldset class="appearance-category"><legend>${escape(t('admin.typographyTitle'))}</legend><p class="field-help">${escape(t('admin.typographyHelp'))}</p><div class="appearance-group-grid">${fontFields}${rangeFields(true)}</div></fieldset>`
    + `<fieldset class="appearance-category"><legend>${escape(t('admin.layoutTitle'))}</legend><div class="appearance-group-grid">${rangeFields(false)}`
    + `<label class="appearance-field"><span>${escape(t('admin.pageSize'))}</span><select class="form-control" name="pageSize">${[10, 25, 50, 100].map((size) => `<option ${settings.pageSize === size ? 'selected' : ''}>${size}</option>`).join('')}</select></label>`
    + toggles.map((key) => `<label class="toggle-label appearance-toggle"><input type="checkbox" name="${key}" class="check" ${settings[key] ? 'checked' : ''}><span>${escape(t('admin.' + key))}</span></label>`).join('') + '</div></fieldset>';
  $('#appearance-fields').querySelectorAll('input[type="range"]').forEach((input) => input.addEventListener('input', () => { input.nextElementSibling.textContent = input.value + ' ' + rangeUnit(input.name); }));
  $('#appearance-fields').querySelectorAll('input[type="color"]').forEach((input) => input.addEventListener('input', () => { input.nextElementSibling.textContent = input.value; }));
  renderWording();
}

/** Labels percentage, duration, and pixel controls in their actual units. */
function rangeUnit(key) { return key.endsWith('FontScale') ? '%' : key === 'toastDuration' ? 's' : 'px'; }

/** Assigns wording to a task-based section, keeping unfamiliar keys available under Other. */
function sectionForWord(key) {
  const prefix = key.split('.')[0];
  return Object.keys(wordSections).find((section) => wordSections[section].includes(prefix)) || 'other';
}

/** Switches wording sections without discarding edits from any section. */
function selectWordSection(section) {
  if (!Object.hasOwn(wordSections, section)) return;
  wordSection = section; renderWording(); $('#wording-fields').scrollTop = 0;
}

/** Displays one searchable wording section and matching counts for the other sections. */
function renderWording() {
  const query = $('#word-search').value.trim().toLowerCase();
  const matches = Object.entries(ui.words).filter(([key, value]) => [key, value, wordDraft[key] || ''].some((text) => text.toLowerCase().includes(query)));
  const counts = Object.fromEntries(Object.keys(wordSections).map((section) => [section, 0]));
  for (const [key] of matches) counts[sectionForWord(key)]++;
  document.querySelectorAll('[data-word-section]').forEach((button) => {
    const selected = button.dataset.wordSection === wordSection;
    button.classList.toggle('active', selected); button.setAttribute('aria-selected', String(selected)); button.tabIndex = selected ? 0 : -1;
    button.querySelector('[data-word-count]').textContent = String(counts[button.dataset.wordSection]);
    if (selected) $('#wording-panel').setAttribute('aria-labelledby', button.id);
  });
  const entries = matches.filter(([key]) => sectionForWord(key) === wordSection).sort(([first], [second]) => first.localeCompare(second));
  $('#word-count').textContent = t('admin.textCount', { count: entries.length });
  $('#wording-fields').innerHTML = entries.map(([key, value]) => `<label class="wording-row"><span class="wording-key">${escape(key)}</span><small>${escape(t('admin.defaultText'))}: ${escape(value)}</small><textarea class="form-control" rows="2" maxlength="2000" data-word-key="${escape(key)}" aria-label="${escape(t('admin.overrideText') + ': ' + key)}" placeholder="${escape(value)}">${escape(Object.hasOwn(wordDraft, key) ? wordDraft[key] : '')}</textarea></label>`).join('') || `<p class="field-help">${escape(t('admin.noWords'))}</p>`;
  $('#wording-fields').querySelectorAll('textarea').forEach((input) => input.addEventListener('input', () => {
    if (input.value === '' || input.value === ui.words[input.dataset.wordKey]) delete wordDraft[input.dataset.wordKey];
    else wordDraft[input.dataset.wordKey] = input.value;
  }));
}

/** Reads the appearance form, preserving only intentional wording overrides. */
function readAppearance() {
  const form = $('#appearance-form'); const result = { ...ui.settings, texts: { ...wordDraft } };
  [...colors, ...fonts].forEach((key) => { result[key] = form.elements[key].value; });
  [...Object.keys(ranges), 'pageSize'].forEach((key) => { result[key] = Number(form.elements[key].value); });
  toggles.forEach((key) => { result[key] = form.elements[key].checked; });
  return result;
}

/** Switches admin sections without navigating or submitting the settings form. */
export function adminTab(name) {
  for (const key of ['appearance', 'wording', 'metadata', 'updates', 'server']) $('#admin-' + key).hidden = key !== name;
  $('#appearance-form').hidden = !['appearance', 'wording'].includes(name);
  $('#removed-movie-history').hidden = name !== 'metadata';
  document.querySelectorAll('[data-admin-tab]').forEach((button) => button.classList.toggle('active', button.dataset.adminTab === name));
  if (name === 'metadata') void pollMetadata();
}

/** Reports metadata-index progress while the admin view remains visible. */
async function pollMetadata() {
  clearTimeout(poll);
  try {
    const status = await api('metadata/status');
    $('#metadata-status').textContent = status.error || (status.total ? t('admin.indexStatus', { done: status.completed, total: status.total, failed: status.failed }) : t('admin.indexIdle'));
    $('#refresh-metadata').disabled = status.running;
    const existing = $('#metadata-unavailable');
    if (existing) existing.remove();
    if (status.unavailable?.length) {
      const affected = document.createElement('details'); affected.id = 'metadata-unavailable'; affected.className = 'metadata-unavailable';
      affected.innerHTML = `<summary>${escape(t('action.viewAffected'))} (${status.unavailable.length})</summary><p class="field-help">${escape(t('admin.indexUnavailable'))}</p><ul>${status.unavailable.map((movie) => `<li>${escape(movie.name)} <a href="https://www.themoviedb.org/movie/${Number(movie.id)}" target="_blank" rel="noopener noreferrer" title="${escape(t('admin.metadataLookup'))}">${escape(t('source.tmdb'))} #${Number(movie.id)}<i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i></a></li>`).join('')}</ul>`;
      $('#metadata-status').closest('.connection-card').append(affected);
    }
    if (status.running && !$('#admin').hidden) poll = setTimeout(pollMetadata, 3000);
  } catch (error) { $('#metadata-status').textContent = error.message; }
}

/** Wires reversible preview, server-wide persistence, configurable wording, metadata refresh, and local score import. */
export function bindAdmin() {
  document.querySelectorAll('[data-admin-tab]').forEach((button) => button.addEventListener('click', () => adminTab(button.dataset.adminTab)));
  document.querySelectorAll('[data-word-section]').forEach((button) => button.addEventListener('click', () => selectWordSection(button.dataset.wordSection)));
  $('#word-sections').addEventListener('keydown', (event) => {
    const button = event.target.closest('[data-word-section]');
    if (!button || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const buttons = [...document.querySelectorAll('[data-word-section]')]; const current = buttons.indexOf(button);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
      : (current + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    selectWordSection(buttons[next].dataset.wordSection); buttons[next].focus();
  });
  $('#word-search').addEventListener('input', renderWording);
  $('#appearance-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    await busy($('#save-appearance'), async () => {
      try {
        ui.settings = await api('appearance', readAppearance()); applyAppearance(ui.settings); applyTexts();
        document.dispatchEvent(new Event('elementarr:appearance-changed')); notify(t('admin.saved'));
      } catch (error) { notify(error.message, true); }
    });
  });
  $('#preview-appearance').addEventListener('click', () => {
    if (!$('#appearance-form').reportValidity()) return;
    ui.settings = readAppearance(); applyAppearance(ui.settings); applyTexts();
    document.dispatchEvent(new Event('elementarr:appearance-changed')); notify(t('admin.previewed'));
  });
  $('#reset-appearance').addEventListener('click', () => { renderAdmin({ ...ui.defaults, texts: wordDraft }); applyAppearance(ui.defaults); notify(t('admin.resetPreview')); });
  $('#reset-wording').addEventListener('click', () => { wordDraft = {}; renderWording(); });
  $('#refresh-metadata').addEventListener('click', async () => {
    await busy($('#refresh-metadata'), async () => {
      try { await api('metadata/refresh', {}); notify(t('admin.indexStarted')); await pollMetadata(); }
      catch (error) { notify(error.message, true); }
    });
  });
  $('#import-ratings').addEventListener('click', async () => {
    await busy($('#import-ratings'), async () => {
      try {
        const file = $('#ratings-file').files[0];
        if (!file || file.size > 30_000) throw new Error(t('admin.invalidImport'));
        const parsed = JSON.parse(await file.text());
        if (!Array.isArray(parsed.movies)) throw new Error(t('admin.invalidImport'));
        const result = await api('ratings/import', parsed); notify(t('admin.imported', { count: result.imported }));
        document.dispatchEvent(new Event('elementarr:rating-imported'));
      } catch (error) { notify(error.message, true); }
    });
  });
}
