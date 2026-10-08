# Contract: Images, publishing and releases

Decisions are in research R2 to R4, R10, R11 and R15.

## Images

| Image | Built from | Base (runtime) | Runs as | Entry |
|---|---|---|---|---|
| `<IMAGE>-api:<version>` | `backend/Dockerfile`, context `.` | `mcr.microsoft.com/dotnet/aspnet:10.0` | `app` | `dotnet InTheKnow.dll` (also `… make-admin`) |
| `<IMAGE>-web:<version>` | `frontend/Dockerfile`, context `.` | `docker.io/library/node:24-alpine` | `node` | `node build` |

- `IMAGE` defaults to `ghcr.io/wjouter/intheknow`. `version` is the release tag without the `v`, for example
  `1.2.0`.
- **Contents**: the published API output, or the web interface's `build/` with its production `node_modules`. There
  are no tests, SDKs, dev dependencies, `.env` files, `.git` or private fixtures. The
  Dockerfiles copy named paths only, and the root `.dockerignore` excludes the rest (research R4).
- **Settings**: from the environment only. No value is baked in at build time.
- **Platforms**: `linux/amd64` and `linux/arm64` under one tag for automatic publishing (FR-003). A manual build has
  the builder's own platform (FR-004a).

## Automatic publishing (GitHub)

| Event | Checks (`ci.yml`) | Images | Release assets |
|---|---|---|---|
| Push to a branch, pull request | run | **none** | none |
| Push of tag `vX.Y.Z` (command line, or a GitHub release that creates it) | run | both, after all checks pass, to `ghcr.io/wjouter/intheknow-{api,web}:X.Y.Z` | `compose.yaml` (pinned to `X.Y.Z`), `env.example` |
| Release published for an existing tag | none | none (no new ref, no push event) | unchanged |
| Any other tag | none | none | none |

- **Credentials**: the workflow's `GITHUB_TOKEN`, with `packages: write` and `contents: write`. No stored registry
  secret (FR-004b).
- **Failure**: if any check fails, nothing is pushed and no asset is uploaded (US4 AS2).
- **One-time maintainer step**: after the first release, set both GHCR packages to public.

## Manual build and publishing (any registry)

The same commands work with `podman` in place of `docker`. Run them from a clone, at the root.

```bash
IMAGE=registry.example.com/me/intheknow   # registry address and image name: an input, not fixed
VERSION=1.2.0
docker build -f backend/Dockerfile  -t "$IMAGE-api:$VERSION" .
docker build -f frontend/Dockerfile -t "$IMAGE-web:$VERSION" .
docker login registry.example.com          # the maintainer's own sign-in, never stored in the repository
docker push "$IMAGE-api:$VERSION"
docker push "$IMAGE-web:$VERSION"
```

- Building without pushing (FR-004) is the two `build` lines. The images then exist only locally.
- Pushing a version that already exists **replaces it** (edge case). `DEPLOY.md` warns against reusing a version.

## Stack definition per release

The repository's `deploy/compose.yaml` refers to `ghcr.io/wjouter/intheknow-{api,web}:VERSION`. One render, used by
both CI and the maintainer:

```bash
sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > compose.yaml
```

- The rendered `compose.yaml` has the image and version as literal text, with no variables (FR-007).
- Operators download `https://github.com/Wjouter/InTheKnow/releases/download/vX.Y.Z/compose.yaml` and
  `…/env.example`.
