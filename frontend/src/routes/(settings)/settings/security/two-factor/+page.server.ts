import { renderSVG } from 'uqr';
import { api } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';

export const load = ({ locals }) => ({ twoFactor: locals.me!.twoFactor, admin: locals.me!.role === 'admin' });

export const actions = {
	start: async (event) => {
		const r = await api<{ secret: string; uri: string }>(event, 'POST', '/api/me/2fa/setup');
		if (!r.ok) return refuse(event, r);
		// The QR code is an image made here, never injected markup (FR-010).
		const qr = `data:image/svg+xml;base64,${btoa(renderSVG(r.data.uri))}`;
		return { secret: r.data.secret, qr };
	},
	enable: async (event) => {
		const r = await api<{ recoveryCodes: string[] }>(event, 'POST', '/api/me/2fa/enable', {
			code: field(await event.request.formData(), 'code')
		});
		if (!r.ok) return refuse(event, r, { form: 'enable' });
		// Shown once, from this action result only; nothing keeps them (FR-034).
		return { recoveryCodes: r.data.recoveryCodes };
	}
};
