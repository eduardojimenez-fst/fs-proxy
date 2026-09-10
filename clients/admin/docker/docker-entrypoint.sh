#!/bin/sh
set -e

# Fail fast on missing required values rather than serve a broken bundle.
: "${FSH_API_URL:?FSH_API_URL is required (e.g. https://api.example.com)}"
: "${FSH_DASHBOARD_URL:?FSH_DASHBOARD_URL is required (e.g. https://app.example.com)}"

# Defaults for non-required values.
: "${FSH_DEFAULT_TENANT:=root}"
# Comma-separated UI module keys to hide (see clients/*/src/lib/modules.ts).
# Empty means hide nothing, which is what an unset variable must mean.
: "${FSH_DISABLED_MODULES:=}"

export FSH_API_URL FSH_DASHBOARD_URL FSH_DEFAULT_TENANT FSH_DISABLED_MODULES

# Render the runtime config from the template, writing into nginx's web root.
envsubst < /usr/share/nginx/html/config.json.template > /usr/share/nginx/html/config.json

# Drop the template so it isn't served accidentally.
rm /usr/share/nginx/html/config.json.template

exec nginx -g 'daemon off;'
