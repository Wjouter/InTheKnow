import { deletePreset, loadPreset, savePreset } from '$lib/server/presets';

export const load = async (event) => ({ preset: await loadPreset(event, '/api/me/presets', 'personal') });

export const actions = {
	save: (event) => savePreset(event, '/api/me/presets'),
	delete: (event) => deletePreset(event, '/api/me/presets', '/presets')
};
