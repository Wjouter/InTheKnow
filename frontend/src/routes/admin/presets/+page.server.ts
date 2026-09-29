import { createPreset, listPresets } from '$lib/server/presets';

export const load = async (event) => ({ presets: await listPresets(event, '/api/admin/presets') });

export const actions = {
	create: (event) => createPreset(event, '/api/admin/presets', '/admin/presets')
};
