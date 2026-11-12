<script lang="ts">
	import PageIntro from '$lib/components/PageIntro.svelte';
	import { dicts } from '$lib/i18n';

	let { data } = $props();
	const t = $derived(dicts[data.lang].about);
	const repo = 'https://github.com/Wjouter/InTheKnow';

	// What ships to users: the web app, the API and the database. Add a runtime dependency here when one is added.
	const credits = [
		{ name: 'Svelte and SvelteKit', url: 'https://svelte.dev', license: 'MIT' },
		{ name: 'Lucide icons', url: 'https://lucide.dev', license: 'ISC' },
		{ name: 'uqr', url: 'https://github.com/unjs/uqr', license: 'MIT' },
		{ name: 'Lato', url: 'https://www.latofonts.com', license: 'SIL Open Font License 1.1' },
		{ name: 'JetBrains Mono', url: 'https://www.jetbrains.com/lp/mono/', license: 'SIL Open Font License 1.1' },
		{ name: 'Node.js', url: 'https://nodejs.org', license: 'MIT' },
		{ name: '.NET', url: 'https://dotnet.microsoft.com', license: 'MIT' },
		{ name: 'Npgsql', url: 'https://www.npgsql.org', license: 'PostgreSQL License' },
		{ name: 'Ical.Net', url: 'https://github.com/ical-org/ical.net', license: 'MIT' },
		{ name: 'Otp.NET', url: 'https://github.com/kspearrin/Otp.NET', license: 'MIT' },
		{ name: 'PostgreSQL', url: 'https://www.postgresql.org', license: 'PostgreSQL License' }
	];
</script>

<svelte:head><title>{t.title} · InTheKnow</title></svelte:head>

<PageIntro title={t.title} intro={t.intro} />
<div class="wrap stack narrow">
	<p class="muted">{t.version(__VERSION__)}</p>
	<section class="stack">
		<h2>{t.licenseTitle}</h2>
		<p>{t.license}</p>
		<p class="links">
			<a href={repo}>{t.source}</a>
			<a href="{repo}/blob/main/LICENSE">{t.licenseText}</a>
		</p>
	</section>
	<section class="stack">
		<h2>{t.creditsTitle}</h2>
		<p>{t.credits}</p>
		<ul>
			{#each credits as c (c.name)}
				<li><a href={c.url}>{c.name}</a> <span class="muted">· {c.license}</span></li>
			{/each}
		</ul>
	</section>
</div>

<style>
	.narrow {
		max-width: 640px;
	}
	h2 {
		font: var(--type-h3);
	}
	.muted {
		color: var(--text-muted);
	}
	.links {
		display: flex;
		gap: var(--space-4);
		flex-wrap: wrap;
	}
	ul {
		margin: 0;
		padding-left: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}
</style>
