# Contract: Frontend configuration and deployment

How the web interface is configured and where it sits next to the API. It extends 001's Transport rules and 004's
[configuration.md](../../004-accounts-login/contracts/configuration.md). Configuration is read from the environment
at startup. The frontend has no secrets of its own.

## Environment

| Variable | Example | Default | Rule |
|---|---|---|---|
| `API_URL` | `http://127.0.0.1:5000` | `http://localhost:5000` | The API's internal address. Same host or container network only, never a public address (001 Transport). Read only on the server (`$env/dynamic/private`). |
| `ORIGIN` | `https://intheknow.example.nl` | none | **Required in production.** The public origin as the browser sees it. adapter-node needs it to build `url.origin` behind the proxy. Without it, `url.origin` is wrong behind the proxy and the CSRF check's `Origin` fallback rejects form posts (research R4). It must equal the API's `Notifications:FrontendBaseUrl`. |
| `ADDRESS_HEADER` | `X-Forwarded-For` | none | Set to `X-Forwarded-For` behind the reverse proxy, so `getClientAddress()` is the browser's address (research R5) |
| `XFF_DEPTH` | `1` | `1` | The number of trusted proxies in front of the frontend. It is 1 for the single reverse proxy. |
| `HOST` | `127.0.0.1` | `0.0.0.0` | **Set it** to loopback or the container network. Only the reverse proxy may reach the frontend directly. |
| `PORT` | `3000` | `3000` | adapter-node's port |

`npm run dev` needs none of these: it uses `http://localhost:5173` and `API_URL`'s default.

## API side (existing 004 keys, set by the operator)

| Key | Value for this feature |
|---|---|
| `InTheKnow__Accounts__TrustedProxies` | The reverse proxy **and the frontend server's** address. Otherwise every user shares one throttling source (004 R3). |
| `InTheKnow__Notifications__FrontendBaseUrl` | The same value as `ORIGIN`. The account and notification emails link there (FR-012). |
| `InTheKnow__Notifications__PublicBaseUrl` | The public origin that serves `/n/…`. Usually the same host as `ORIGIN`. |

## Reverse proxy (TLS-terminating, same host)

| Path | Goes to |
|---|---|
| `/n/…` | The API (003's public pages) |
| `/api/…` | **Nowhere.** It is not exposed: the browser never reaches the API (FR-003, 001 Transport). Return `404`. |
| everything else | The frontend |

The proxy MUST:
- terminate TLS, and redirect `http` to `https` (Principle II);
- send `Strict-Transport-Security: max-age=31536000`. HSTS belongs to the TLS endpoint, not the app (research R16);
- set `X-Forwarded-For` to the client address, replacing any value the client sent;
- pass `Host` unchanged, or match `ORIGIN`;
- not log query strings for the frontend's `/account/…` paths, because they carry tokens (FR-047 applies to the
  app's own logs; the proxy's logs are the operator's to configure).

## Headers the app sends (research R16)

Every response gets:
- `X-Frame-Options: DENY`;
- `Referrer-Policy: no-referrer`;
- `X-Content-Type-Options: nosniff`;
- `Cache-Control: no-store` on every response except the hashed `/_app/immutable/*` assets;
- the CSP from `svelte.config.js` (`kit.csp`), including `frame-ancestors 'none'`.

The proxy must not strip or override them.

## Logs (FR-047)

stdout, one line per request:

```text
2026-10-01T08:30:02.114Z GET /calendars/12/notifications 200 84ms
```

Errors go to stderr as `time path name: message`. They never contain:
- query strings, cookies or headers;
- IP addresses or `User-Agent`;
- the user;
- tokens, passwords or email addresses.

## Constants (not configurable)

| Constant | Value | Source |
|---|---|---|
| Changes page size | 50 | research R11 |
| API call timeout | 30 s | research R7 |
| `itk_session` max age | 400 days (the API's idle lifetime decides validity) | research R3 |
| `itk_pending` max age | 10 min | 004 awaiting-code lifetime |
| `itk_invite` max age | 1 h | research R15 |
| Bot check load timeout before the "couldn't load" message | 10 s | research R15 |
| Phone layout breakpoint | below 768 px | research R18 |
| Minimum supported width | 360 px | SC-007 |
