import { describe, expect, it } from 'vitest';
import { formatter } from './format';

const now = new Date('2026-09-27T12:00:00Z');

describe('formatter', () => {
	it('keeps local time across the DST change in Europe/Amsterdam', () => {
		const f = formatter('en', 'Europe/Amsterdam', now);
		expect(f.time('2026-10-24T06:30:00Z')).toBe('08:30'); // CEST, UTC+2
		expect(f.time('2026-10-26T07:30:00Z')).toBe('08:30'); // CET, UTC+1
	});

	it('shows the year only for another year', () => {
		const f = formatter('en', 'Europe/Amsterdam', now);
		expect(f.dateTime('2026-10-01T06:30:00Z')).toBe('Thu 1 Oct 08:30');
		expect(f.date('2027-01-05T10:00:00Z')).toContain('2027');
		expect(f.date('2026-10-01T06:30:00Z')).not.toContain('2026');
	});

	it('compares years in the user’s zone', () => {
		const instant = '2026-12-31T12:00:00Z';
		expect(formatter('en', 'Pacific/Auckland', now).date(instant)).toContain('2027');
		expect(formatter('en', 'UTC', now).date(instant)).not.toContain('2026');
	});

	it('uses the 24-hour clock in both languages, and an en dash for ranges', () => {
		for (const lang of ['en', 'nl'] as const) {
			const f = formatter(lang, 'Europe/Amsterdam', now);
			expect(f.time('2026-10-01T13:05:00Z')).toBe('15:05');
			expect(f.range('2026-10-01T06:30:00Z', '2026-10-01T08:15:00Z')).toMatch(/08:30–10:15$/);
		}
		expect(formatter('nl', 'Europe/Amsterdam', now).dateTime('2026-10-01T06:30:00Z')).toBe('do 1 okt 08:30');
	});

	it('words waits', () => {
		expect(formatter('en', 'UTC').wait(90)).toBe('2 minutes');
		expect(formatter('en', 'UTC').wait(30)).toBe('30 seconds');
		expect(formatter('nl', 'UTC').wait(90)).toBe('2 minuten');
		expect(formatter('nl', 'UTC').wait(30)).toBe('30 seconden');
	});

	// 010 FR-001, FR-002, research R1.
	describe('found', () => {
		const at = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString();
		const banned = ['yesterday', 'today', 'gisteren', 'vandaag'];

		it('is relative under 24 hours and exact from 24 hours, in both languages', () => {
			const en = formatter('en', 'Europe/Amsterdam', now);
			const nl = formatter('nl', 'Europe/Amsterdam', now);
			expect(en.found(at(59)).text).toBe('59 seconds ago');
			expect(en.found(at(59 * 60 + 59)).text).toBe('59 minutes ago');
			expect(en.found(at(23 * 3600 + 59 * 60)).text).toBe('23 hours ago');
			expect(en.found(at(24 * 3600)).text).toBe(en.dateTime(at(24 * 3600)));
			expect(nl.found(at(59)).text).toBe('59 seconden geleden');
			expect(nl.found(at(90)).text).toBe('1 minuut geleden');
			expect(nl.found(at(23 * 3600 + 59 * 60)).text).toBe('23 uur geleden');
			expect(nl.found(at(24 * 3600)).text).toBe(nl.dateTime(at(24 * 3600)));
		});

		it('never says yesterday or today, and clamps a time just ahead of ours', () => {
			for (const lang of ['en', 'nl'] as const) {
				const f = formatter(lang, 'Europe/Amsterdam', now);
				for (const s of [0, 1, 3600, 86399, 86400, 86400 * 2, 86400 * 400])
					for (const word of banned) expect(f.found(at(s)).text.toLowerCase()).not.toContain(word);
				expect(f.found(at(-5)).text).toBe(f.found(at(0)).text);
			}
		});

		it('shows the year for another year, and keeps the exact value with the year', () => {
			const f = formatter('en', 'Europe/Amsterdam', now);
			const lastYear = '2025-11-10T08:19:00Z';
			expect(f.found(lastYear).text).toBe('Mon 10 Nov 2025 09:19');
			expect(f.found(at(60)).exact).toBe(f.fullDateTime(at(60)));
			expect(f.found(at(60)).iso).toBe(at(60));
			expect([f.found(at(86399)).relative, f.found(at(86400)).relative]).toEqual([true, false]);
		});
	});

	it('fullDateTime always has the year', () => {
		expect(formatter('en', 'Europe/Amsterdam', now).fullDateTime('2026-10-01T06:30:00Z')).toBe('Thu 1 Oct 2026 08:30');
		expect(formatter('nl', 'Europe/Amsterdam', now).fullDateTime('2026-10-01T06:30:00Z')).toBe('do 1 okt 2026 08:30');
	});

	// 010 FR-012, research R11.
	it('words the retention period', () => {
		expect(formatter('en', 'UTC').retention(365)).toBe('1 year');
		expect(formatter('en', 'UTC').retention(730)).toBe('2 years');
		expect(formatter('en', 'UTC').retention(90)).toBe('90 days');
		expect(formatter('nl', 'UTC').retention(365)).toBe('1 jaar');
		expect(formatter('nl', 'UTC').retention(90)).toBe('90 dagen');
	});
});
