# Build from the repository root: docker build -f frontend/Dockerfile .  (specs/006 research R3)
# The build stage runs on the build machine's platform; the output and production packages are pure JS/CSS.
FROM --platform=$BUILDPLATFORM docker.io/library/node:24-alpine AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build
RUN mkdir /prod && cp package.json package-lock.json /prod/ && npm ci --omit=dev --ignore-scripts --prefix /prod

# No RUN here, and no init process: adapter-node handles SIGTERM. Settings come from the environment only.
FROM docker.io/library/node:24-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /src/build build/
COPY --from=build /prod/node_modules node_modules/
COPY --from=build /src/package.json ./
USER node
CMD ["node", "build"]
