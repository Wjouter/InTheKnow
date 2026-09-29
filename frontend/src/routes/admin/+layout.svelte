<script lang="ts">
	import PageIntro from '$lib/components/PageIntro.svelte';
	import TabNav from '$lib/components/TabNav.svelte';
	import { dicts } from '$lib/i18n';

	let { data, children } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<PageIntro title={t.admin.title} />
<div class="wrap">
	<TabNav
		label={t.admin.title}
		items={[
			{ href: '/admin/accounts', label: t.admin.accounts.tab },
			{ href: '/admin/sign-up', label: t.admin.signUp.tab },
			{ href: '/admin/presets', label: t.admin.presets.tab },
			{ href: '/admin/calendars', label: t.admin.calendars.tab },
			{ href: '/admin/email', label: t.admin.email.tab }
		]}
	>
		{@render children()}
	</TabNav>
</div>
