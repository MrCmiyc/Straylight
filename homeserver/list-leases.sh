#!/usr/bin/env bash
# List all Technitium DHCP leases (address / hostname / type), sorted by IP.
set -euo pipefail
tok=$(cat ~/straylight/.technitium_token)
curl -fsS "http://localhost:5380/api/dhcp/leases/list?token=$tok" \
  | jq -r '.response.leases[] | "\(.address)\t\(.hostName)\t\(.type)"' \
  | sort -t. -k1,1n -k2,2n -k3,3n -k4,4n
