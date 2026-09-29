import type { Dict } from '$lib/i18n';

// The signed-in nav, shared by Header and PhoneNav.
export function navItems(t: Dict, admin: boolean) {
	const items = [
		{ href: '/changes', label: t.nav.changes, icon: 'changes', active: (p: string) => p === '/changes' },
		{
			href: '/calendars',
			label: t.nav.calendars,
			icon: 'calendars',
			active: (p: string) => p.startsWith('/calendars') || p.startsWith('/presets')
		},
		{
			href: '/settings/account',
			label: t.nav.settings,
			icon: 'settings',
			active: (p: string) => p.startsWith('/settings') || p === '/notifications'
		}
	] as const;
	const adminItem = { href: '/admin/accounts', label: t.nav.admin, icon: 'admin', active: (p: string) => p.startsWith('/admin') } as const;
	return admin ? [...items, adminItem] : [...items];
}
