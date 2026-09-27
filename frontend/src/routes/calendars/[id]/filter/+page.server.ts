import { fail } from '@sveltejs/kit';
import { api, must } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';
import { calendarId } from '$lib/server/calendar';
import { editorRules, parseRules, ruleSentence, type Rule } from '$lib/server/rules';
import type { Refusal } from '$lib/refusal';

type Filter = {
	source: { type: 'preset'; presetId: number; name: string; scope: string } | { type: 'custom' };
	rules: Rule[];
	inactiveRules: number[];
	activeConditions: string[];
	values: Record<string, string[]>;
};
type Preset = { id: number; name: string; scope: 'personal' | 'global'; isDefault?: true; rules: Rule[] };

export const load = async (event) => {
	const id = calendarId(event);
	const [filter, presets] = await Promise.all([
		api<Filter>(event, 'GET', `/api/me/calendars/${id}/filter`),
		api<{ items: Preset[] }>(event, 'GET', '/api/me/presets')
	]);
	const f = must(event, filter, { notFound: 'notInAccount' });
	const t = event.locals.t;
	return {
		source: f.source.type === 'preset' ? { preset: f.source.name, presetId: f.source.presetId } : { preset: null, presetId: null },
		rules: editorRules(f.rules, f.inactiveRules, f.activeConditions, null, t),
		// The editor starts again from the saved rules whenever they change.
		rulesKey: JSON.stringify(f.rules),
		activeConditions: f.activeConditions,
		values: f.values,
		presets: must(event, presets).items.map((p) => ({ id: p.id, name: p.name, scope: p.scope, sentences: p.rules.map((r) => ruleSentence(r, t)) }))
	};
};

export const actions = {
	saveRules: async (event) => {
		const parsed = parseRules(await event.request.formData());
		if ('reason' in parsed) return fail(400, { reason: parsed.reason, message: event.locals.t.reasons.invalid_rules, values: { form: 'rules' } } satisfies Refusal);
		const r = await api(event, 'PUT', `/api/me/calendars/${calendarId(event)}/filter`, { rules: parsed.rules });
		if (!r.ok) return refuse(event, r, { form: 'rules' });
		return { saved: 'rules' } as const;
	},
	applyPreset: async (event) => {
		const presetId = Number(field(await event.request.formData(), 'presetId'));
		const r = await api(event, 'PUT', `/api/me/calendars/${calendarId(event)}/filter/preset`, { presetId });
		if (!r.ok) return refuse(event, r, { form: 'preset' });
		return { saved: 'preset' } as const;
	},
	// Saving a calendar's filter as a preset is a read of its rules followed by a create (002 contracts/api.md).
	saveAsPreset: async (event) => {
		const name = field(await event.request.formData(), 'name');
		const filter = await api<Filter>(event, 'GET', `/api/me/calendars/${calendarId(event)}/filter`);
		if (!filter.ok) return refuse(event, filter, { form: 'asPreset', name });
		const r = await api(event, 'POST', '/api/me/presets', { name, rules: filter.data.rules });
		if (!r.ok) return refuse(event, r, { form: 'asPreset', name });
		return { saved: 'asPreset' } as const;
	}
};
