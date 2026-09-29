<script lang="ts">
	import EmailRequest from '$lib/components/EmailRequest.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{t.forgot.title} · InTheKnow</title></svelte:head>

<EmailRequest title={t.forgot.title} intro={t.forgot.intro} submit={t.forgot.submit} done={t.forgot.done} botCheck={data.botCheck} {form} />
