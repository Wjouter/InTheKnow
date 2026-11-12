import { error } from '@sveltejs/kit';
import { parseAll, parseId } from '$lib/history';
import { loadHistory } from '$lib/server/history';

// The full history page of the lesson of change {id} (010 contracts/pages.md § Full page). An expired session goes to
// sign-in with `next` set to this address through api(); every 404 cause shows the same page (FR-016).
export const load = async (event) => {
	const id = parseId(event.params.id);
	if (id === null) error(404, { kind: 'notFound', message: event.locals.t.errors.notFoundTitle });
	const all = parseAll(event.url.searchParams.get('all'));
	return { history: await loadHistory(event, id), openedId: Number(id), all };
};
