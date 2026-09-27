import type { Dict, Lang } from '$lib/i18n';

declare global {
	type Me = {
		email: string;
		role: 'user' | 'admin';
		language: Lang;
		twoFactor: boolean;
		recoveryCodesLeft: number;
		hasPassword: boolean;
		pendingEmail: string | null;
		homePage: 'changes' | 'combined';
	};

	namespace App {
		interface Locals {
			token: string | null;
			me: Me | null;
			tz: string;
			lang: Lang;
			t: Dict;
			unavailable: boolean;
		}
		// From the root layout. Dictionaries hold functions, so pages look them up as dicts[page.data.lang].
		interface PageData {
			lang: Lang;
			me: Me | null;
		}
		// A notice for the next page, handed over by an action that redirects there (a removal).
		interface PageState {
			notice?: string;
		}
		interface Error {
			message: string;
			kind?: 'locked' | 'notAllowed' | 'notInAccount' | 'unavailable' | 'notFound';
			email?: string;
		}
	}
}

export {};
