#!/bin/sh
set -e

: "${FSH_API_URL:?FSH_API_URL is required (e.g. https://api.example.com)}"
: "${FSH_DEFAULT_TENANT:=root}"
# Comma-separated UI module keys to hide (see src/lib/modules.ts).
# Empty means hide nothing, which is what an unset variable must mean.
: "${FSH_DISABLED_MODULES:=}"

export FSH_API_URL FSH_DEFAULT_TENANT FSH_DISABLED_MODULES

envsubst < /usr/share/nginx/html/config.json.template \
       > /usr/share/nginx/html/config.json
rm /usr/share/nginx/html/config.json.template

exec nginx -g 'daemon off;'
