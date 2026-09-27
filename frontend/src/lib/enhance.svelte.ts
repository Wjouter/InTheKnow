import type { ActionResult, SubmitFunction } from '@sveltejs/kit';

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
 * the form as it is and opens the step-up dialog; `resubmit()` then sends the same form again (FR-020).
 */
export function submitter(opts: { onResult?: (r: ActionResult) => void } = {}) {
	let pending = $state(false);
	let stepUpOpen = $state(false);
	let last: { form: HTMLFormElement; submitter: HTMLElement | null } | null = null;

	const enhance: SubmitFunction = ({ formElement, submitter: button }) => {
		pending = true;
		last = { form: formElement, submitter: button };
		return async ({ result, update }) => {
			if (isStepUp(result)) {
				await update({ reset: false });
				stepUpOpen = true;
			} else {
				await update();
			}
			pending = false;
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
