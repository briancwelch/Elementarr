import { $, app, api, busy, notify } from './core.js';

/** Displays public release status and the installation's automatic-update preferences. */
export async function loadUpdates(refreshPreferences = true) {
  const value = await api('updates');
  if (refreshPreferences) { $('#updates-enabled').checked = value.enabled; $('#updates-auto').checked = value.autoInstall; }
  $('#updates-version').textContent = `Installed version: ${value.status.currentVersion}${value.status.availableVersion ? ' | Available: ' + value.status.availableVersion : ''}`;
  $('#updates-status').textContent = !value.canInstall ? 'Docker/local development uses source builds. Automatic installation is available in the Windows service.'
    : value.status.message || 'Public automatic updates are ready. No GitHub token is required.';
  $('#updates-check').disabled = value.status.busy;
  $('#updates-install').disabled = !value.canInstall || value.status.busy || !value.status.availableVersion;
}

/** Wires explicit preferences/check/install actions while automatic background checks run in the installed Windows service. */
export function bindUpdates() {
  $('#updates-form').addEventListener('submit', async (event) => {
    event.preventDefault(); await busy(event.submitter, async () => {
      try { await api('updates/settings', { enabled: $('#updates-enabled').checked, autoInstall: $('#updates-auto').checked });
        await loadUpdates(); notify('Update preferences saved.');
      } catch (error) { notify(error.message, true); }
    });
  });
  $('#updates-check').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try { await api('updates/check', {}); await loadUpdates(); } catch (error) { notify(error.message, true); }
  }));
  $('#updates-install').addEventListener('click', (event) => busy(event.currentTarget, async () => {
    try { await api('updates/install', {}); notify('The verified update is queued. Elementarr will restart when installation begins.', false, true); await loadUpdates(); }
    catch (error) { notify(error.message, true); }
  }));
  setInterval(() => { if (!document.hidden && !$('#app-view').hidden && app.view === 'settings') void loadUpdates(false).catch(() => {}); }, 10000);
}
