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
});
