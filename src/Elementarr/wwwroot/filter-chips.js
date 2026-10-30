import { Chips } from './vendor/bootstrap/bootstrap.bundle.min.js';
import { $, escape } from './core.js';
import { t } from './ui.js';

const pickers = new Map();
const languageNames = new Intl.DisplayNames(['en'], { type: 'language' });
const countryNames = new Intl.DisplayNames(['en'], { type: 'region' });
const genreIcons = {
  Action: 'explosion', Adventure: 'compass', Animation: 'wand-magic-sparkles', Comedy: 'face-laugh',
  Crime: 'fingerprint', Documentary: 'video', Drama: 'masks-theater', Family: 'people-roof', Fantasy: 'dragon',
  History: 'landmark', Horror: 'ghost', Music: 'music', Mystery: 'magnifying-glass', Romance: 'heart',
  'Science Fiction': 'rocket', Thriller: 'bolt', 'TV Movie': 'tv', War: 'jet-fighter', Western: 'hat-cowboy',
  'Action & Adventure': 'compass', Kids: 'children', News: 'newspaper', Reality: 'users',
  'Sci-Fi & Fantasy': 'rocket', Soap: 'heart', Talk: 'comments', 'War & Politics': 'landmark'
};

/** Supplies readable suggestions while accepting other valid language and country codes. */
export function internationalChoices(television) {
  return {
    language: 'en fr es de it ja ko zh hi pt ru sv da nl ar tr fi no pl uk cs el he th vi id ms'.split(' ')
      .map(value => ({ value, label: languageNames.of(value), icon: 'language' })),
    country: 'US GB CA AU NZ FR DE IT ES JP KR CN IN BR MX AR IE AT BE CH DK FI NL NO PL PT SE ZA'.split(' ')
      .map(value => ({ value, label: countryNames.of(value), icon: 'globe' })),
    certification: (television ? ['TV-Y', 'TV-Y7', 'TV-Y7-FV', 'TV-G', 'TV-PG', 'TV-14', 'TV-MA', 'NR']
      : ['G', 'PG', 'PG-13', 'R', 'NC-17', 'NR', 'U', '12', '12A', '15', '18', 'R18'])
      .map(value => ({ value, label: value, icon: 'shield-halved' }))
  };
}

/** Renders a labeled Bootstrap chip input with native searchable suggestions and a query-safe hidden value. */
export function chipField(name, label, choices, theme = 'primary') {
  const id = 'filter-' + name;
  return `<div class="filter-field filter-chip-field" data-chip-filter="${name}"><label for="${id}">${escape(t(label))}</label><div class="chip-input theme-${theme}"><input id="${id}" class="form-ghost" list="${id}-options" autocomplete="off" maxlength="80" placeholder="${escape(t('filter.chooseValues'))}" aria-describedby="${id}-help"></div><datalist id="${id}-options">${choices.map(choice => `<option value="${escape(choice.label || choice.name)}" data-value="${escape(choice.value || choice.name)}" data-icon="${escape(choice.icon || genreIcons[choice.name] || 'tags')}">${escape(choice.value || '')}</option>`).join('')}</datalist><input type="hidden" name="${name}"><small id="${id}-help" class="filter-chip-help">${escape(t(name === 'language' || name === 'country' ? 'filter.chipCodeHelp' : name === 'certification' ? 'filter.chipRatingHelp' : 'filter.chipHelp'))}</small></div>`;
}

/** Resolves a suggestion or validates an additional ISO code or regional certification without accepting arbitrary genres. */
function resolveChoice(picker, supplied) {
  const text = supplied.trim();
  const found = picker.choices.find(choice => [choice.value, choice.label].some(value => value.toLowerCase() === text.toLowerCase()));
  if (found) return found;
  let choice;
  if (picker.name === 'country' && /^[a-z]{2}$/i.test(text)) {
    const value = text.toUpperCase(), label = countryNames.of(value);
    if (label !== value) choice = { value, label, icon: 'globe' };
  } else if (picker.name === 'language' && /^[a-z]{2,3}$/i.test(text)) {
    const value = text.toLowerCase(), label = languageNames.of(value);
    if (label !== value || value === 'xx') choice = { value, label: value === 'xx' ? 'No linguistic content' : label, icon: 'language' };
  } else if (picker.name === 'certification' && /^[\p{L}\p{N}][\p{L}\p{N}+ -]{0,11}$/u.test(text)) {
    choice = { value: text.toUpperCase(), label: text.toUpperCase(), icon: 'shield-halved' };
  }
  if (choice) picker.choices.push(choice);
  return choice;
}

/** Binds the bundled Bootstrap Chips component, its hidden form value, icons, and constrained suggestions. */
export function bindChipFilters() {
  for (const picker of pickers.values()) picker.chips.dispose();
  pickers.clear();
  for (const field of $('#filter-fields').querySelectorAll('[data-chip-filter]')) {
    const container = field.querySelector('.chip-input'), input = field.querySelector('.form-ghost');
    const picker = { name: field.dataset.chipFilter, input, hidden: field.querySelector('input[type="hidden"]'),
      choices: [...field.querySelectorAll('option')].map(option => ({ value: option.dataset.value, label: option.value, icon: option.dataset.icon })) };
    picker.chips = new Chips(container, { separator: null, createOnBlur: true, maxChips: 32 });
    container.addEventListener('add.bs.chips', event => {
      const choice = resolveChoice(picker, event.value);
      if (!choice) { event.preventDefault(); input.setCustomValidity(t('filter.chooseKnownValue')); return; }
      input.setCustomValidity('');
      if (event.value !== choice.label) { event.preventDefault(); picker.chips.add(choice.label); }
    });
    container.addEventListener('change.bs.chips', () => {
      picker.hidden.value = picker.chips.getValues().map(label => picker.choices.find(choice => choice.label === label).value).join(',');
      for (const chip of container.querySelectorAll('.chip')) {
        const choice = picker.choices.find(choice => choice.label === chip.dataset.bsChipValue);
        if (!chip.querySelector('.chip-icon')) {
          const icon = document.createElement('i'); icon.className = `chip-icon fa-solid fa-${choice.icon}`;
          icon.setAttribute('aria-hidden', 'true'); chip.prepend(icon);
        }
        chip.querySelector('.chip-dismiss').setAttribute('aria-label', t('filter.removeValue', { value: choice.label }));
      }
    });
    input.addEventListener('input', () => input.setCustomValidity(''));
    input.addEventListener('change', () => {
      if (input.value.trim()) { picker.chips.add(input.value); if (input.checkValidity()) input.value = ''; }
    });
    pickers.set(picker.name, picker);
  }
}

/** Restores chips from active form values after opening, clearing, changing view, or dismissing a summary chip. */
export function writeChipFilters() {
  for (const picker of pickers.values()) {
    const values = picker.hidden.value.split(',').filter(Boolean);
    picker.chips.clear();
    for (const value of values) picker.chips.add(value);
    picker.input.value = ''; picker.input.setCustomValidity('');
  }
}

/** Commits typed selections before applying filters, keeping invalid choices out of API requests. */
export function commitChipFilters() {
  for (const picker of pickers.values()) {
    if (picker.input.value.trim()) { picker.chips.add(picker.input.value); if (picker.input.checkValidity()) picker.input.value = ''; }
  }
  return $('#filter-form').reportValidity();
}

/** Returns a readable selected value and icon for the active-filter summary. */
export function chipChoice(name, value) {
  return pickers.get(name)?.choices.find(choice => choice.value.toLowerCase() === String(value).toLowerCase());
}
