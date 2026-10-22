# syntax=docker/dockerfile:1
# ---- build ----
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build && npm test

# ---- runtime: Nitro node server, SSR + /api proxy to NUXT_API_BASE (CONTRACT §8.1, §8.4) ----
FROM node:24-alpine AS runtime
WORKDIR /app
# NUXT_API_BASE is required at runtime (compose: http://api:8080); the server refuses to start without it.
ENV NODE_ENV=production \
    HOST=0.0.0.0 \
    PORT=3000
COPY --from=build /app/.output ./.output
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:3000/login >/dev/null || exit 1
CMD ["node", ".output/server/index.mjs"]
