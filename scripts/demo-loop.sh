#!/usr/bin/env bash
set -euo pipefail

URL="${1:-https://ha-demo.apps.uat-ocp4.uat.corp.cableone.net}"
INTERVAL="${INTERVAL:-0.2}"

declare -A pod_counts
total=0

print_totals() {
    echo ""
    echo "========== TOTALS =========="
    for pod in $(echo "${!pod_counts[@]}" | tr ' ' '\n' | sort); do
        printf "  %-45s %d hits\n" "$pod" "${pod_counts[$pod]}"
    done
    printf "  %-45s %d\n" "TOTAL" "$total"
    echo "============================"
}

trap 'print_totals; exit 0' INT

printf "Polling %s every %ss  —  Ctrl-C for totals\n\n" "$URL" "$INTERVAL"
printf "%-8s  %-45s  %-6s  %s\n" "#" "POD" "READY" "POD-HITS"
printf "%s\n" "--------  ---------------------------------------------  ------  --------"

while true; do
    resp=$(curl -sk --max-time 2 "$URL/" 2>/dev/null) || {
        printf "%-8s  %-45s  %-6s  %s\n" "$total" "ERROR" "-" "-"
        sleep "$INTERVAL"
        continue
    }

    pod=$(printf '%s' "$resp"  | jq -r '.pod   // "unknown"' 2>/dev/null || echo "unknown")
    ready=$(printf '%s' "$resp" | jq -r '.ready // "?"'      2>/dev/null || echo "?")

    pod_counts["$pod"]=$(( ${pod_counts["$pod"]:-0} + 1 ))
    total=$(( total + 1 ))

    printf "%-8d  %-45s  %-6s  %d\n" \
        "$total" "$pod" "$ready" "${pod_counts[$pod]}"

    sleep "$INTERVAL"
done
