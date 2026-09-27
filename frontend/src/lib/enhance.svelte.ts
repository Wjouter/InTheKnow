import type { ActionResult, SubmitFunction } from '@sveltejs/kit';
import { tick } from 'svelte';
import { goto } from '$app/navigation';

/** A failure that asks for the step-up dialog (research R8). */
export function isStepUp(result: ActionResult): boolean {
	return (
		result.type === 'failure' &&
		result.status === 403 &&
		typeof result.data === 'object' &&
		result.data !== null &&
		result.data.stepUp === true
	);
}

/**
 * The shared `use:enhance` callback: `pending` while a submission is in flight (FR-044). On a step-up refusal it keeps
 * the form as it is and opens the step-up dialog; `resubmit()` then sends the same form again (FR-020). With `notice`,
 * a redirect to `notice.at` shows that notice there (not, say, a redirect to sign-in after the session ended).
 */
export function submitter(opts: { onResult?: (r: ActionResult) => void; notice?: { at: string; text: () => string } } = {}) {
	let pending = $state(false);
	let stepUpOpen = $state(false);
	let last: { form: HTMLFormElement; submitter: HTMLElement | null } | null = null;

	const enhance: SubmitFunction = ({ formElement, submitter: button }) => {
		pending = true;
		last = { form: formElement, submitter: button };
		const focused = document.activeElement;
		return async ({ result, update }) => {
			if (isStepUp(result)) {
				await update({ reset: false });
				stepUpOpen = true;
			} else if (result.type === 'redirect' && opts.notice && new URL(result.location, location.href).pathname === opts.notice.at) {
				// The same navigation as update() does, with a notice for the page it lands on (root layout).
				await goto(result.location, { invalidateAll: true, state: { notice: opts.notice.text() } });
			} else {
				await update();
			}
			pending = false;
			// The button was disabled while pending, which drops keyboard focus to the page (FR-043, FR-044). Give it
			// back, unless the result went to another page or something else took focus meanwhile.
			if (result.type !== 'redirect' && focused instanceof HTMLElement && focused.isConnected) {
				await tick();
				if (document.activeElement === document.body) focused.focus();
			}
			opts.onResult?.(result);
		};
	};

	return {
		get pending() {
			return pending;
		},
		get stepUpOpen() {
			return stepUpOpen;
		},
		set stepUpOpen(v: boolean) {
			stepUpOpen = v;
		},
		enhance,
		resubmit() {
			stepUpOpen = false;
			if (last) last.form.requestSubmit(last.submitter as HTMLButtonElement | null);
		}
	};
}
