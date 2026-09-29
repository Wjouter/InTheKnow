import { describe, expect, it } from 'vitest';
import { pickLang } from '.';

describe('pickLang', () => {
	it.each([
		['nl-NL,en;q=0.8', 'nl'],
		['en-US,nl;q=0.5', 'en'],
		['de,nl;q=0.9,en;q=0.8', 'nl'],
		['de', 'en'],
		[null, 'en']
	])('Accept-Language %s gives %s', (header, lang) => {
		expect(pickLang(undefined, header)).toBe(lang);
	});

	it('prefers the itk_lang cookie, and ignores an unknown one', () => {
		expect(pickLang('nl', 'en')).toBe('nl');
		expect(pickLang('de', 'en')).toBe('en');
		expect(pickLang('de', 'nl')).toBe('nl');
	});
});
