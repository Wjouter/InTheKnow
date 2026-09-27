<script lang="ts">
	import PageIntro from '$lib/components/PageIntro.svelte';
	import TabNav from '$lib/components/TabNav.svelte';
	import { dicts } from '$lib/i18n';

	let { data, children } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<PageIntro title={t.settings.title} />
<div class="wrap">
	<TabNav
		label={t.settings.tabsLabel}
		items={[
			{ href: '/settings/account', label: t.settings.account },
			{ href: '/settings/security', label: t.settings.security },
			{ href: '/settings/preferences', label: t.settings.preferences },
			{ href: '/notifications', label: t.settings.notifications }
		]}
	>
		{@render children()}
	</TabNav>
</div>
