import { deletePreset, loadPreset, savePreset } from '$lib/server/presets';

export const load = async (event) => ({ preset: await loadPreset(event, '/api/admin/presets', 'global') });

export const actions = {
	save: (event) => savePreset(event, '/api/admin/presets'),
	delete: (event) => deletePreset(event, '/api/admin/presets', '/admin/presets')
};
