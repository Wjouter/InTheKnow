import { en } from './en';
import { nl } from './nl';

export type Lang = 'en' | 'nl';

// Every string leaf becomes `string`, every function leaf keeps its signature. `nl: Dict` then fails svelte-check on a
// missing or an extra key (SC-008).
type Widen<T> = T extends string
	? string
	: T extends (...args: infer A) => infer R
		? (...args: A) => R
		: { [K in keyof T]: Widen<T[K]> };
export type Dict = Widen<typeof en>;

export const dicts: Record<Lang, Dict> = { en, nl };

const isLang = (v: string | undefined | null): v is Lang => v === 'en' || v === 'nl';

/** itk_lang when it is en/nl, else the first of nl/en in Accept-Language by q-value, else en (research R9). */
export function pickLang(cookie: string | undefined, acceptLanguage: string | null): Lang {
	if (isLang(cookie)) return cookie;
	const ranked = (acceptLanguage ?? '')
		.split(',')
		.map((part, i) => {
			const [tag, ...params] = part.trim().split(';');
			const q = params.map((p) => p.trim()).find((p) => p.startsWith('q='));
			return { lang: tag.trim().toLowerCase().split('-')[0], q: q ? Number(q.slice(2)) || 0 : 1, i };
		})
		.sort((a, b) => b.q - a.q || a.i - b.i);
	return ranked.find((r) => isLang(r.lang) && r.q > 0)?.lang as Lang | undefined ?? 'en';
}
