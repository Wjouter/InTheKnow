# Quickstart: validate the unsubscribe footer

## Automated

```bash
cd backend && dotnet test
```

Expected: all green, including the footer/href assertions for Changes and Test ([contract](./contracts/email-footer.md)).

Snapshots: after regenerating with `INTHEKNOW_UPDATE_SNAPSHOTS=1 dotnet test --filter TextPartMatchesSnapshot`,

```bash
git diff --stat backend/InTheKnow.Tests/EmailSnapshots/
```

must list exactly `changes.en.txt`, `changes.nl.txt`, `test.en.txt`, `test.nl.txt` (FR-007: no confirmation or
account email changed).

## Manual (Mailpit, from `compose.yaml`)

1. Start the dev stack, sign in, add and confirm an email channel (Mailpit catches the confirmation).
2. Send a test notification; in Mailpit check the HTML and Text tabs: footer sentence, then an underlined grey
   "Unsubscribe" link / `Unsubscribe: http://localhost…/n/unsubscribe/…` line.
3. Open the link: the one-button page shows; the channel is still *active* in the notifications settings (SC-003).
4. Press the button: the page says done; the channel is *unsubscribed*. Press again via the back button: same
   "done" page, no error.
5. Record a calendar change: Mailpit receives nothing for that channel (SC-004).
6. Switch the account language to Dutch and repeat step 2: footer text and "Afmelden" in Dutch.
