<script lang="ts">
	import ExternalLink from '@lucide/svelte/icons/external-link';
	import X from '@lucide/svelte/icons/x';
	import { page } from '$app/state';
	import History from './History.svelte';
	import { dicts } from '$lib/i18n';
	import { historyHref } from '$lib/history';

	// The history as a card over the feed (010 research R8). It is open exactly while page.state.history is set: every
	// way to close it steps back, and Back clears the state, which unmounts it. Not Dialog.svelte: that one owns `open`.
	let { data }: { data: NonNullable<App.PageState['history']> } = $props();

	const t = $derived(dicts[page.data.lang]);
	const all = $derived(page.state.historyAll ?? data.all);
	let el: HTMLDialogElement | undefined = $state();
	const id = $props.id();

	const back = () => history.back();

	$effect(() => {
		if (!el) return;
		const dialog = el;
		dialog.showModal();
		// The backdrop is the <dialog> itself; its content never is (research R8).
		const onBackdrop = (e: MouseEvent) => {
			if (e.target === dialog) back();
		};
		dialog.addEventListener('click', onBackdrop);
		return () => {
			dialog.removeEventListener('click', onBackdrop);
			if (dialog.open) dialog.close();
		};
	});
</script>

<dialog
	bind:this={el}
	aria-labelledby="{id}-title"
	oncancel={(e) => {
		e.preventDefault();
		back();
	}}
	onclose={() => {
		// Only a close the browser did on its own; a close because Back cleared the state must not step back again.
		if (page.state.history) back();
	}}
>
	<div class="head">
		<div class="title">
			<h2 id="{id}-title">{data.history.name}</h2>
			<span class="calendar">{data.history.calendarName}</span>
		</div>
		<a class="page-link" href={historyHref(data.openedId, all)} data-sveltekit-reload>
			<ExternalLink size={16} aria-hidden="true" />{t.history.openAsPage}
		</a>
		<button type="button" class="close" onclick={back} aria-label={t.close}><X size={20} aria-hidden="true" /></button>
	</div>
	<div class="body">
		<History history={data.history} openedId={data.openedId} all={data.all} cap={20} />
	</div>
</dialog>

<style>
	dialog {
		width: calc(100% - 2 * var(--space-4));
		max-width: 720px;
		max-height: 90vh;
		padding: 0;
		border: 0;
		border-top: 6px solid var(--brand);
		border-radius: var(--radius-md);
		box-shadow: var(--shadow-3);
		background: var(--white);
		color: var(--text-body);
		overflow: hidden;
	}
	dialog[open] {
		display: flex;
		flex-direction: column;
	}
	dialog::backdrop {
		background: var(--overlay-scrim);
	}
	.head {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
		padding: var(--space-4) var(--space-3) var(--space-3) var(--space-6);
		border-bottom: 1px solid var(--border-subtle);
	}
	.title {
		flex: 1;
		min-width: 0;
		display: flex;
		flex-direction: column;
	}
	h2 {
		font: var(--type-h3);
		overflow-wrap: anywhere;
	}
	.calendar {
		font: var(--type-small);
		color: var(--text-muted);
	}
	.page-link {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		min-height: var(--hit-min);
		font: var(--type-small);
		font-weight: 700;
		color: var(--text-link);
	}
	.close {
		flex: none;
		width: var(--hit-min);
		height: var(--hit-min);
		display: inline-flex;
		align-items: center;
		justify-content: center;
		border: 0;
		background: transparent;
		color: var(--text-strong);
		border-radius: var(--radius-md);
		cursor: pointer;
	}
	.close:hover {
		background: var(--brand-soft);
	}
	.body {
		overflow-y: auto;
		padding: var(--space-4) var(--space-6) var(--space-6);
	}
	@media (max-width: 599px) {
		dialog {
			width: 100%;
			max-width: none;
			height: 100%;
			max-height: none;
			border-radius: 0;
		}
		.head {
			padding-left: var(--space-4);
		}
		.body {
			padding: var(--space-4);
		}
	}
</style>
