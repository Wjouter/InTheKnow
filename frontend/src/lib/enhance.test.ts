import { describe, expect, it } from 'vitest';
import { isStepUp } from './enhance.svelte';

describe('isStepUp', () => {
	it('is true only for a failure that asks for step-up', () => {
		expect(isStepUp({ type: 'failure', status: 403, data: { stepUp: true } })).toBe(true);
		expect(isStepUp({ type: 'failure', status: 403, data: { reason: 'admin_required' } })).toBe(false);
		expect(isStepUp({ type: 'failure', status: 400, data: { stepUp: true } })).toBe(false);
		expect(isStepUp({ type: 'failure', status: 403, data: undefined })).toBe(false);
		expect(isStepUp({ type: 'success', status: 200, data: { stepUp: true } })).toBe(false);
		expect(isStepUp({ type: 'redirect', status: 303, location: '/' })).toBe(false);
		expect(isStepUp({ type: 'error', error: new Error('x') })).toBe(false);
	});

	it('is false for data: null', () => {
		expect(isStepUp({ type: 'failure', status: 403, data: null as never })).toBe(false);
	});
});
