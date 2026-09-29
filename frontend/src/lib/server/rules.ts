import type { Dict } from '$lib/i18n';

// Rules in words, and the rule editor's server side (research R14, 002 contracts/rules.md).

export type Rule = { outcome: string; when: Record<string, unknown> };
export type EditorRule = Rule & { sentence: string; inactive: boolean; inactiveReason: string | null; error: string | null };

const strings = (v: unknown): string[] => (Array.isArray(v) ? v.map(String) : typeof v === 'string' ? [v] : []);

function word(map: Record<string, string>, v: string): string {
	return Object.hasOwn(map, v) ? map[v] : v;
}

function condition(key: string, value: unknown, when: Record<string, unknown>, t: Dict): string | null {
	const w = t.rules;
	const or = (items: string[]) => items.join(w.or);
	switch (key) {
		case 'changeKind':
			return w.cond.changeKind(or(strings(value).map((k) => word(w.kinds, k))));
		case 'eventEnded':
			return w.cond.eventEnded;
		case 'statusFrom':
			return when.statusTo === undefined
				? w.cond.statusFrom(word(w.statuses, String(value)))
				: w.cond.statusFromTo(word(w.statuses, String(value)), word(w.statuses, String(when.statusTo)));
		case 'statusTo':
			return when.statusFrom === undefined ? w.cond.statusTo(word(w.statuses, String(value))) : null; // said with statusFrom
		case 'nameContains':
			return w.cond.nameContains(String(value));
		case 'activityType':
			return w.cond.activityType(or(strings(value).map((v) => word(w.types, v))));
		case 'group':
			return w.cond.group(or(strings(value)));
		case 'room':
			return w.cond.room(or(strings(value)));
		case 'teacher':
			return w.cond.teacher(or(strings(value)));
		default:
			return w.cond.unknown;
	}
}

/** "Hide changes when the change is to the location and the activity type is holiday". */
export function ruleSentence(rule: Rule, t: Dict): string {
	const when = rule.when && typeof rule.when === 'object' ? rule.when : {};
	const parts = Object.entries(when)
		.map(([k, v]) => condition(k, v, when, t))
		.filter((p): p is string => p !== null);
	const outcome = word(t.rules.outcomes, rule.outcome);
	return parts.length ? t.rules.sentence(outcome, parts.join(t.rules.and)) : outcome;
}

/** Why a rule doesn't apply to this calendar, or null when it does (FR-006). */
export function inactiveReason(ruleIndex: number, inactiveRules: number[], activeConditions: string[], t: Dict): string | null {
	if (!inactiveRules.includes(ruleIndex)) return null;
	// On a MyTimetables calendar only teacher rules can be inactive: teacher detection is off.
	return activeConditions.includes('activityType') ? t.rules.inactiveTeacher : t.rules.inactiveMyTimetables;
}

/** The editor's hidden `rules` field. The API stays the validator; this only needs an array. */
export function parseRules(data: FormData): { rules: unknown[] } | { reason: 'invalid_rules' } {
	try {
		const rules: unknown = JSON.parse(String(data.get('rules') ?? ''));
		return Array.isArray(rules) ? { rules } : { reason: 'invalid_rules' };
	} catch {
		return { reason: 'invalid_rules' };
	}
}

export function editorRules(
	rules: Rule[],
	inactiveRules: number[],
	activeConditions: string[],
	error: { rule?: number; message: string } | null,
	t: Dict
): EditorRule[] {
	return rules.map((rule, i) => {
		const reason = inactiveReason(i, inactiveRules, activeConditions, t);
		return {
			...rule,
			sentence: ruleSentence(rule, t),
			inactive: reason !== null,
			inactiveReason: reason,
			error: error?.rule === i ? error.message : null
		};
	});
}
