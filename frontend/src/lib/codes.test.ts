import { describe, expect, it } from 'vitest';
import { channelFailureMessage, checkFailureMessage, checkFailures, reasonMessage, reasons } from './codes';
import { dicts } from './i18n';

// SC-009: every reason code documented in the API contracts has a message.
const files = import.meta.glob('../../../specs/00[1-5]-*/contracts/api.md', {
	query: '?raw',
	import: 'default',
	eager: true
}) as Record<string, string>;
const contracts = Object.keys(files);

// Codes the contracts list that the frontend never receives.
const excluded: string[] = [
	'invalid_subscriber' // gone since 004: there is no subscriber id in the path any more
];

/** Reasons appear in table cells, as `409 demote_first`, and inline (`reason`: `invalid_url`). */
function extract(markdown: string): string[] {
	const codes = new Set<string>();
	for (const line of markdown.split('\n')) {
		if (!/^\s*([|*-])/.test(line) || !/\b[45]\d\d\b/.test(line)) continue;
		for (const [, token] of line.matchAll(/`([^`]+)`/g)) {
			const code = token.replace(/^\d{3} /, '');
			if (code.startsWith('validation_failed:')) codes.add(code);
			else if (/^[a-z]+(_[a-z0-9]+)+$/.test(code)) codes.add(code);
		}
	}
	return [...codes];
}

describe('reason codes', () => {
	const known = new Set<string>(reasons);

	it('reads the five contracts', () => expect(contracts).toHaveLength(5));

	it.each(contracts)('covers every reason in %s', (path) => {
		const codes = extract(files[path]);
		expect(codes.length).toBeGreaterThan(0);
		const missing = codes.filter((code) => {
			if (excluded.includes(code)) return false;
			if (code.startsWith('validation_failed:')) {
				const c = code.slice('validation_failed:'.length);
				return c !== '<code>' && !(checkFailures as readonly string[]).includes(c);
			}
			return !known.has(code);
		});
		expect(missing).toEqual([]);
	});

	it('has a message for every reason and check failure in both languages', () => {
		for (const t of Object.values(dicts)) {
			for (const r of reasons) expect(t.reasons[r].length).toBeGreaterThan(0);
			for (const c of checkFailures) expect(t.checkFailures[c].length).toBeGreaterThan(0);
		}
	});
});

describe('pattern codes', () => {
	const t = dicts.en;

	it('words calendar check failures', () => {
		expect(checkFailureMessage('http_404', t)).toBe(t.patterns.linkGone);
		expect(checkFailureMessage('http_410', t)).toBe(t.patterns.linkGone);
		expect(checkFailureMessage('http_401', t)).toMatch(/refused the link/);
		expect(checkFailureMessage('http_403', t)).toBe(t.patterns.linkRefused);
		expect(checkFailureMessage('http_500', t)).toBe(t.patterns.serverError);
		expect(checkFailureMessage('validation_failed:empty_feed', t)).toBe(t.checkFailures.empty_feed);
		expect(t.patterns.linkGone).toMatch(/no longer exists/);
	});

	it('words channel failures', () => {
		expect(channelFailureMessage('smtp_550', t)).toMatch(/rejected this address/);
		expect(channelFailureMessage('smtp_421', t)).toMatch(/refused it for now/);
		expect(channelFailureMessage('unreachable', t)).toMatch(/can’t be reached/);
	});

	it('gives the generic message for an unknown code', () => {
		expect(checkFailureMessage('bogus', t)).toBe(t.reasons.unknown);
		expect(channelFailureMessage('bogus', t)).toBe(t.reasons.unknown);
		expect(reasonMessage('bogus', t)).toBe(t.reasons.unknown);
	});
});
