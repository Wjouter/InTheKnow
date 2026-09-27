import { error, fail, redirect, type RequestEvent } from '@sveltejs/kit';
import type { Refusal } from '$lib/refusal';
import { api, must } from './api';
import { field, refuse } from './actions';
import { editorRules, parseRules, ruleSentence, type Rule } from './rules';

// Personal presets (/presets, /api/me/presets) and global ones (/admin/presets, /api/admin/presets) share this code.

export type Preset = { id: number; name: string; scope: 'personal' | 'global'; isDefault?: true; rules: Rule[]; followers?: number };

export async function listPresets(event: RequestEvent, base: string) {
	const t = event.locals.t;
	return must(event, await api<{ items: Preset[] }>(event, 'GET', base)).items.map((p) => ({
		id: p.id,
		name: p.name,
		scope: p.scope,
		isDefault: p.isDefault ?? false,
		followers: p.followers ?? null,
		sentences: p.rules.map((r) => ruleSentence(r, t))
	}));
}

/** One preset of the list at `base`, for its edit page. Missing, or of the wrong scope, is "not found". */
export async function loadPreset(event: RequestEvent, base: string, scope: Preset['scope']) {
	const id = /^[1-9]\d{0,17}$/.test(event.params.id ?? '') ? Number(event.params.id) : 0;
	const preset = must(event, await api<{ items: Preset[] }>(event, 'GET', base)).items.find((p) => p.id === id && p.scope === scope);
	if (!preset) error(404, { kind: 'notFound', message: event.locals.t.errors.notFoundTitle });
	return {
		id: preset.id,
		name: preset.name,
		isDefault: preset.isDefault ?? false,
		followers: preset.followers ?? 0,
		rules: editorRules(preset.rules, [], [], null, event.locals.t),
		rulesKey: JSON.stringify(preset.rules)
	};
}

export async function createPreset(event: RequestEvent, base: string, pageBase: string) {
	const data = await event.request.formData();
	const name = field(data, 'name');
	// A copy takes the rules of the preset it copies (002 contracts/api.md).
	let rules: Rule[] = [];
	const from = Number(field(data, 'copyOf'));
	if (from) {
		const list = await api<{ items: Preset[] }>(event, 'GET', base);
		if (!list.ok) return refuse(event, list, { form: `copy-${from}`, name });
		rules = list.data.items.find((p) => p.id === from)?.rules ?? [];
	}
	const r = await api<Preset>(event, 'POST', base, { name, rules });
	if (!r.ok) return refuse(event, r, { form: from ? `copy-${from}` : 'create', name });
	redirect(303, `${pageBase}/${r.data.id}`);
}

export async function savePreset(event: RequestEvent, base: string) {
	const data = await event.request.formData();
	const name = field(data, 'name');
	const parsed = parseRules(data);
	if ('reason' in parsed) return fail(400, { reason: parsed.reason, message: event.locals.t.reasons.invalid_rules, values: { name } } satisfies Refusal);
	const r = await api(event, 'PUT', `${base}/${event.params.id}`, { name, rules: parsed.rules });
	if (!r.ok) return refuse(event, r, { name });
	return { saved: true } as const;
}

export async function deletePreset(event: RequestEvent, base: string, pageBase: string) {
	const r = await api(event, 'DELETE', `${base}/${event.params.id}`);
	if (!r.ok) return refuse(event, r);
	redirect(303, pageBase);
}
