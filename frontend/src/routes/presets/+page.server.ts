import { createPreset, listPresets } from '$lib/server/presets';

export const load = async (event) => {
	const presets = await listPresets(event, '/api/me/presets');
	return { personal: presets.filter((p) => p.scope === 'personal'), global: presets.filter((p) => p.scope === 'global') };
};

export const actions = {
	create: (event) => createPreset(event, '/api/me/presets', '/presets'),
	copy: (event) => createPreset(event, '/api/me/presets', '/presets')
};
