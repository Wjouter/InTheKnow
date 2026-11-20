# Quickstart: verify dark emails

What each step proves is named in brackets. Formats are in [contracts/email-theme.md](./contracts/email-theme.md),
colours in [research.md R4](./research.md#r4-the-dark-palette-fr-003-fr-005).

## Automated checks

```bash
dotnet test --project backend/InTheKnow.Tests --filter-class "*EmailChannelTests" --filter-class "*AccountEmailTests" --filter-class "*NotificationApiTests" --filter-class "*DeliveryTests"
```

Expected: all pass, including per theme: metas and media block (FR-001, FR-007, FR-008), palette and dark contrast
(FR-005, SC-003), hooks and dark block in step, text part and headers equal across themes (FR-011), worst case under
100 KB in each theme (FR-012, SC-007), the API field and its refusals (FR-013), and the theme read at send time
(FR-010).

```bash
cd frontend && npm test && npm run check && npm run test:e2e
```

Expected: all pass. The Preferences save sends `emailTheme` and leaves the `itk_theme` cookie alone.

## Send every email in every theme to Mailpit

```bash
docker compose up -d mailpit
```

```bash
INTHEKNOW_PREVIEW_SMTP_PORT=1025 dotnet test --project backend/InTheKnow.Tests --filter-class "*EmailPreviewTests"
```

Expected: Mailpit at http://localhost:8025 has every fixture in English and Dutch, three times: addressed
`{name}.{language}.{theme}@example.test`.

- In a browser set to dark, open a `device` email's raw HTML: it is dark. Set the browser to light: it is light, and
  matches the `light` email exactly (SC-002, FR-002).
- Open a `dark` email's raw HTML in a browser set to light: it is dark (FR-008).
- Mailpit's **HTML Check** tab: features flagged for `prefers-color-scheme` and `color-scheme` are expected; nothing
  new beyond those (FR-006).

## Real mail apps (SC-001, SC-004, SC-006)

Send from a running instance to a test mailbox you can open in each app: on Preferences, choose the email theme,
then **Send test** on the email channel. For account emails, trigger one (for example a password change). Do it for
each theme.

| App (Clarifications) | Device dark | Device light | Expected |
|---|---|---|---|
| Apple Mail (iOS or macOS) | `device` | `device` | dark version / light version; switching the device switches the email (SC-001, US1-3) |
| Apple Mail | `dark`, device light | `light`, device dark | stays dark / stays light (SC-006) |
| Gmail web | any theme | — | `device` and `light` show the light email; `dark` shows dark; all readable (SC-004, US2-1) |
| Gmail app (Android), device dark | each theme | — | readable top to bottom, button recognisable (SC-004, US2-2) |
| New Outlook / Outlook on the web, dark | each theme | — | readable top to bottom, button recognisable (SC-004, US2-2) |

For the apps in the last three rows also check at 320px wide and at desktop width: no sideways scrolling (US2-3).
This covers the open manual check T027 of spec 007.

## Preferences (SC-005, US3)

1. Open Settings › Preferences on an account that never set it: "Same as my device" is chosen (US3-1).
2. Choose "Always dark", Save, send a test notification: it arrives dark (US3-6). Time the change: under 15 s
   (SC-005).
3. Change the web app theme on the same page: the email theme stays, and the other way round (US3-7).
