import type { Cookies } from '@sveltejs/kit';
import { invalidateAll } from '$app/navigation';

// The theme chosen on this device (011 research R2 to R5). No cookie means "same as this device".

export type Theme = 'light' | 'dark';

/** The cookie through an allow-list: anything else is "same as this device" (FR-016). */
export const pickTheme = (value: string | undefined): Theme | null => (value === 'light' || value === 'dark' ? value : null);

/** `light` or `dark` stores the choice, `device` removes it, anything else changes nothing. */
export function storeTheme(cookies: Cookies, choice: string | null) {
	if (choice === 'light' || choice === 'dark') {
		// Same attributes as itk_lang: not HttpOnly and not personal data (data-model.md).
		cookies.set('itk_theme', choice, { httpOnly: false, secure: true, sameSite: 'lax', path: '/', maxAge: 31_536_000 });
	} else if (choice === 'device') {
		cookies.delete('itk_theme', { path: '/' });
	}
}

/** Applies the choice at once, has the server store it, and tells the other tabs (R4, R5). */
export async function setTheme(choice: 'light' | 'dark' | 'device') {
	document.documentElement.dataset.theme = choice === 'device' ? '' : choice;
	await fetch('/theme', { method: 'POST', body: new URLSearchParams({ theme: choice }), redirect: 'manual' });
	if (typeof BroadcastChannel !== 'undefined') {
		const channel = new BroadcastChannel('itk_theme');
		channel.postMessage(choice);
		channel.close();
	}
	await invalidateAll();
}
