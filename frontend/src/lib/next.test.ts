import { describe, expect, it } from 'vitest';
import { safeNext } from './next';

const origin = 'http://localhost:5173';

describe('safeNext', () => {
	it.each(['/calendars/1', '/calendars/1/notifications?x=1', '/'])('keeps %s', (next) => {
		expect(safeNext(next, origin)).toBe(next);
	});

	it.each([
		'//evil.example',
		'/\\evil.example',
		'https://evil.example',
		'https:evil.example',
		'javascript:alert(1)',
		'%2F%2Fevil.example',
		'/%5Cevil.example',
		'',
		null
	])('turns %s into /', (next) => {
		expect(safeNext(next, origin)).toBe('/');
	});
});
