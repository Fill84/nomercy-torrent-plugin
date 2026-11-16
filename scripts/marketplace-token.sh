#!/usr/bin/env bash
#
# Prints a short-lived access token for the marketplace's publisher API.
#
# The publisher API takes the publisher's own Keycloak token and nothing else:
# a listing answers only to the account that owns it. CI cannot log in, so it
# holds a Keycloak *offline* token of that account (scripts/marketplace-login.py
# stores it as the MARKETPLACE_REFRESH_TOKEN secret) and trades it here for an
# access token that lives a few minutes.
#
# An offline token lapses when it is not used for a while, which for a plugin
# that is released every few weeks is the normal case. The weekly keep-alive
# workflow calls this with --keepalive so the token is used, and so a token that
# did lapse turns a scheduled run red long before a release needs it.
#
# Usage:
#   scripts/marketplace-token.sh              # prints the access token
#   scripts/marketplace-token.sh --keepalive  # prints nothing, fails if dead
#
# Environment:
#   MARKETPLACE_REFRESH_TOKEN  required
#   MARKETPLACE_AUTH           default https://auth.nomercy.tv/realms/NoMercyTV
#   MARKETPLACE_CLIENT         default nomercy-ui
set -euo pipefail

: "${MARKETPLACE_REFRESH_TOKEN:?MARKETPLACE_REFRESH_TOKEN is not set. Run scripts/marketplace-login.py once to store it as a secret of this repository.}"

realm="${MARKETPLACE_AUTH:-https://auth.nomercy.tv/realms/NoMercyTV}"
client="${MARKETPLACE_CLIENT:-nomercy-ui}"

command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

answer="$(curl -sS \
    --data-urlencode "grant_type=refresh_token" \
    --data-urlencode "client_id=$client" \
    --data-urlencode "refresh_token=$MARKETPLACE_REFRESH_TOKEN" \
    "$realm/protocol/openid-connect/token")"

access="$(printf '%s' "$answer" | jq -r '.access_token // empty')"

if [ -z "$access" ]; then
    echo "Keycloak would not trade the marketplace token:" >&2
    printf '%s' "$answer" | jq -r '"  \(.error // "no error given"): \(.error_description // "")"' >&2 || printf '%s\n' "$answer" >&2
    echo "Run scripts/marketplace-login.py again to store a new one." >&2
    exit 1
fi

if [ "${1:-}" = "--keepalive" ]; then
    echo "marketplace token: alive"
    exit 0
fi

printf '%s\n' "$access"
