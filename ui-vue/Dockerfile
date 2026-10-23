# syntax=docker/dockerfile:1
# ---- build: type-check + Vite production build ----
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build && npm test

# ---- runtime: nginx serves the SPA and proxies /api to the API service (CONTRACT §8.1) ----
FROM nginx:1.27-alpine AS runtime
# nginx.conf is an envsubst template (official image entrypoint): only TRUSTED_* variables are substituted, so nginx's own
# $variables stay untouched. TRUSTED_PROXY_IP = the reverse proxy (caddy) allowed to set the client IP; compose overrides it.
ENV NGINX_ENVSUBST_FILTER=^TRUSTED_ \
    TRUSTED_PROXY_IP=172.30.0.20
COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -qO- http://127.0.0.1/healthz >/dev/null || exit 1
CMD ["nginx", "-g", "daemon off;"]
