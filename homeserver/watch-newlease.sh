#!/usr/bin/env bash
# Watch Technitium DHCP for a NEW device joining the network. Prints it and exits.
set -euo pipefail
tok=$(cat ~/straylight/.technitium_token)
leases(){ curl -fsS "http://localhost:5380/api/dhcp/leases/list?token=$tok" \
  | jq -r '.response.leases[] | "\(.address)\t\(.hostName)\t\(.type)"'; }

declare -A seen
while IFS= read -r line; do ip=${line%%$'\t'*}; seen[$ip]=1; done < <(leases)
echo "baseline ${#seen[@]} leases; watching ~6 min for a new device..."

found=0
for i in $(seq 1 36); do
  sleep 10
  while IFS= read -r line; do
    ip=${line%%$'\t'*}
    if [ -z "${seen[$ip]:-}" ]; then echo "NEW DEVICE: $line"; seen[$ip]=1; found=1; fi
  done < <(leases)
  [ "$found" = 1 ] && exit 0
done
echo "timeout: no new device appeared in ~6 min"
