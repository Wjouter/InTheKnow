import type { RequestEvent } from '@sveltejs/kit';
import { api } from './api';
import { field, refuse } from './actions';

/** A keyset cursor from the query: only a positive integer. */
export const after = (url: URL) => (/^[1-9]\d{0,17}$/.test(url.searchParams.get('after') ?? '') ? url.searchParams.get('after')! : null);

/** An admin action on one item: the id comes from the form field `idField` and is checked to be a number. */
export function itemAction(idField: string, method: string, path: (id: string) => string, done: string, body?: (data: FormData) => unknown) {
	return async (event: RequestEvent) => {
		const data = await event.request.formData();
		const id = field(data, idField);
		if (!/^[1-9]\d{0,17}$/.test(id)) return refuse(event, { ok: false, status: 400, reason: 'unknown', extra: {} });
		const r = await api(event, method, path(id), body?.(data));
		if (!r.ok) return refuse(event, r, { [idField]: id });
		return { done };
	};
}
