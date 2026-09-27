<script lang="ts">
	import { beforeNavigate } from '$app/navigation';
	import { page } from '$app/state';
	import Button from './Button.svelte';
	import { dicts } from '$lib/i18n';

	// Recovery codes, shown once (US6-2, spec edge case "Recovery codes not saved"): copy, download, and a warning
	// before leaving the page while they are shown.
	let { codes }: { codes: string[] } = $props();
	const t = $derived(dicts[page.data.lang]);
	let saved = $state(false);
	let copied = $state(false);
	const text = $derived(codes.join('\n') + '\n');

	beforeNavigate(({ cancel, willUnload }) => {
		if (!saved && !willUnload && !confirm(t.recovery.leave)) cancel();
	});
	$effect(() => {
		if (saved) return;
		const warn = (e: BeforeUnloadEvent) => e.preventDefault();
		addEventListener('beforeunload', warn);
		return () => removeEventListener('beforeunload', warn);
	});

	async function copy() {
		await navigator.clipboard.writeText(text);
		copied = true;
	}
</script>

{#if !saved}
	<section class="codes section" aria-labelledby="recovery-title">
		<h2 id="recovery-title">{t.recovery.title}</h2>
		<p class="notice warning">{t.recovery.warning}</p>
		<ol>
			{#each codes as code (code)}<li><code>{code}</code></li>{/each}
		</ol>
		<div class="row">
			<Button type="button" variant="secondary" onclick={copy}>{copied ? t.recovery.copied : t.recovery.copy}</Button>
			<a class="download" href="data:text/plain;charset=utf-8,{encodeURIComponent(text)}" download="intheknow-recovery-codes.txt">{t.recovery.download}</a>
			<Button type="button" onclick={() => (saved = true)}>{t.recovery.done}</Button>
		</div>
	</section>
{/if}

<style>
	h2 {
		font: var(--type-h4);
	}
	ol {
		margin: 0;
		padding-left: var(--space-6);
		columns: 2;
		column-gap: var(--space-8);
	}
	code {
		font: var(--type-time);
		color: var(--text-strong);
		line-height: 2;
	}
	.download {
		display: inline-flex;
		align-items: center;
		min-height: var(--hit-min);
		font-weight: var(--fw-bold);
	}
	@media (max-width: 400px) {
		ol {
			columns: 1;
		}
	}
</style>
