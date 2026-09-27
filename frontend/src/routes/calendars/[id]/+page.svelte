<script lang="ts">
	import CalendarStatus from '$lib/components/CalendarStatus.svelte';
	import ConfirmAction from '$lib/components/ConfirmAction.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<div class="stack">
	{#if r}<p class="notice error" role="alert">{r.message}</p>{/if}
	<CalendarStatus calendar={data.calendar} />
	<div>
		<ConfirmAction
			label={t.calendars.remove}
			title={t.calendars.removeTitle}
			confirm={t.calendars.removeConfirm}
			action="?/remove"
			enhanceWith={s.enhance}
			pending={s.pending}
		>
			<p>{t.calendars.removeBody}</p>
		</ConfirmAction>
	</div>
</div>
