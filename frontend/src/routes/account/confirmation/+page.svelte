<script lang="ts">
	import EmailRequest from '$lib/components/EmailRequest.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{t.confirmation.title} · InTheKnow</title></svelte:head>

<EmailRequest
	title={t.confirmation.title}
	intro={t.confirmation.intro}
	submit={t.confirmation.submit}
	done={t.confirmation.done}
	botCheck={data.botCheck}
	{form}
/>
