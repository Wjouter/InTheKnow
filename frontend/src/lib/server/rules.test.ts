import { describe, expect, it } from 'vitest';
import { dicts } from '$lib/i18n';
import { editorRules, inactiveReason, parseRules, ruleSentence, type Rule } from './rules';

// Every outcome and condition of 002 contracts/rules.md.
const rules: Rule[] = [
	{ outcome: 'showOnly', when: { changeKind: ['added', 'removed', 'name', 'time', 'status', 'location', 'description'] } },
	{ outcome: 'hide', when: { changeKind: ['activityType', 'groups', 'teachers'] } },
	{ outcome: 'lowPriority', when: { statusFrom: 'tentative', statusTo: 'confirmed' } },
	{ outcome: 'hide', when: { statusFrom: 'none' } },
	{ outcome: 'hide', when: { statusTo: 'cancelled' } },
	{ outcome: 'hide', when: { eventEnded: true } },
	{ outcome: 'hide', when: { nameContains: 'Zelfstudie' } },
	{ outcome: 'hide', when: { activityType: ['holiday', 'lesson', 'test', 'appointment'], changeKind: ['location'] } },
	{ outcome: 'showOnly', when: { group: ['ALTI26VT4ES1'] } },
	{ outcome: 'hide', when: { room: ['AL00.A1.38', 'AL00.A1.20'] } },
	{ outcome: 'lowPriority', when: { teacher: ['Jansen, Piet'] } }
];
const rawKeys = ['changeKind', 'statusFrom', 'statusTo', 'eventEnded', 'nameContains', 'activityType', 'showOnly', 'lowPriority', '{', '[', 'null', 'undefined'];

describe('ruleSentence', () => {
	for (const lang of ['en', 'nl'] as const) {
		it.each(rules.map((r) => [JSON.stringify(r.when), r] as const))(`words %s in ${lang}`, (_, rule) => {
			const s = ruleSentence(rule, dicts[lang]);
			expect(s.length).toBeGreaterThan(10);
			for (const raw of rawKeys) expect(s).not.toContain(raw);
		});
	}

	it('reads as a sentence', () => {
		expect(ruleSentence(rules[7], dicts.en)).toBe(
			'Hide changes when the activity type is holiday or lesson or test or appointment and the change is to the location'
		);
		expect(ruleSentence(rules[2], dicts.en)).toBe('Mark changes as low priority when the status changes from tentative to confirmed');
	});
});

describe('inactive rules', () => {
	it('gives the reason for an inactive rule only', () => {
		const generic = ['changeKind', 'eventEnded', 'status', 'nameContains'];
		expect(inactiveReason(0, [1], generic, dicts.en)).toBeNull();
		expect(inactiveReason(1, [1], generic, dicts.en)).toBe(dicts.en.rules.inactiveMyTimetables);
		expect(inactiveReason(1, [1], [...generic, 'activityType', 'group', 'room'], dicts.en)).toBe(dicts.en.rules.inactiveTeacher);
		const view = editorRules(rules.slice(0, 3), [1], generic, { rule: 2, message: 'bad' }, dicts.en);
		expect(view.map((r) => [r.inactive, r.error])).toEqual([
			[false, null],
			[true, null],
			[false, 'bad']
		]);
	});
});

describe('parseRules', () => {
	const form = (rules: string) => {
		const data = new FormData();
		data.set('rules', rules);
		return data;
	};
	it('needs a JSON array', () => {
		expect(parseRules(form('not json'))).toEqual({ reason: 'invalid_rules' });
		expect(parseRules(form('{}'))).toEqual({ reason: 'invalid_rules' });
		expect(parseRules(new FormData())).toEqual({ reason: 'invalid_rules' });
		expect(parseRules(form('[{"outcome":"hide","when":{"eventEnded":true}}]'))).toEqual({
			rules: [{ outcome: 'hide', when: { eventEnded: true } }]
		});
	});
});
