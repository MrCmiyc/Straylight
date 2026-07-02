#!/usr/bin/env bash
# Block/unblock Discord on the local Technitium DNS server via its HTTP API.
# Installed on mqtt-host (the DNS host); driven by cron. Usage: discord-block.sh {block|unblock|status}
set -euo pipefail

SERVER="http://localhost:5380"
HERE="$(cd "$(dirname "$0")" && pwd)"
TOKEN_FILE="$HERE/.technitium_token"
LOG="$HERE/discord-schedule.log"

DOMAINS=(discord.com discordapp.com discord.gg discordapp.net discord.media discord.gift discordcdn.com dis.gd)

TOKEN="$(cat "$TOKEN_FILE")"
TS="$(date '+%Y-%m-%d %H:%M:%S')"

api() { # $1 = endpoint, $2 = extra query string (optional)
  curl -fsS "${SERVER}/api/${1}?token=${TOKEN}${2:-}"
}

case "${1:-status}" in
  block)
    for d in "${DOMAINS[@]}"; do api "blocked/add" "&domain=${d}" >/dev/null; done
    echo "$TS  OK block (${#DOMAINS[@]} domains)" >> "$LOG"
    echo "blocked ${#DOMAINS[@]} domains"
    ;;
  unblock)
    for d in "${DOMAINS[@]}"; do api "blocked/delete" "&domain=${d}" >/dev/null; done
    echo "$TS  OK unblock" >> "$LOG"
    echo "unblocked ${#DOMAINS[@]} domains"
    ;;
  status)
    for d in "${DOMAINS[@]}"; do
      if api "blocked/list" "&domain=${d}" | grep -q '"type":"SOA"'; then
        printf '%-18s BLOCKED\n' "$d"
      else
        printf '%-18s allowed\n' "$d"
      fi
    done
    ;;
  *)
    echo "usage: $0 {block|unblock|status}" >&2; exit 1 ;;
esac
