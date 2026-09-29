import { redirect } from '@sveltejs/kit';
import { api, clearPending, clearSession } from '$lib/server/api';

export const load = () => redirect(303, '/');

export const actions = {
	default: async (event) => {
		// Signed out in this browser even when the API call fails (FR-006, US1-8).
		await api(event, 'POST', '/api/auth/sign-out', undefined, { on401: 'return' });
		clearSession(event);
		clearPending(event);
		redirect(303, '/');
	}
};
