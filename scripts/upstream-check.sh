#!/usr/bin/env bash
# upstream-check.sh — Track upstream G-Helper Windows commits for Linux port relevance
#
# Usage:
#   ./scripts/upstream-check.sh              # Check for new commits, show categorized list
#   ./scripts/upstream-check.sh --details    # Also show diffs for relevant commits
#   ./scripts/upstream-check.sh --mark       # Save current upstream HEAD as "last checked"
#   ./scripts/upstream-check.sh --reset      # Reset to current upstream HEAD without review
#
# Requires: upstream G-Helper repo cloned at ../g-helper (relative to this repo root)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
UPSTREAM_DIR="$(cd "$REPO_ROOT/../g-helper" 2>/dev/null && pwd)" || { echo "ERROR: Upstream repo not found at ../g-helper"; exit 1; }
STATE_FILE="$REPO_ROOT/.upstream-last-checked"

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; DIM='\033[2m'; BOLD='\033[1m'; NC='\033[0m'

# Keywords for auto-categorization
SKIP_PATTERNS='mouse|ally|xgm|xg.mobile|crowdin|readme|screenshot|slash.light|dynamic.light|anime|peripher|dpi.profile|polling.rate|gladius|keris|harpe|strix.impact|spatha|signature.update'
RELEVANT_PATTERNS='fan|mode|aura|rgb|gpu|eco|camera|touchpad|fnlock|fn.lock|power.limit|acpi|wmi|hotkey|keyboard|brightness|config|notification|overdrive|miniled|mini.led|refresh|mux|thermal|cpu|battery|charge|aspm|backlight|clamshell|startup|tray|performance|silent|turbo|balanced'

show_details=false
mark_checked=false
reset_mode=false

for arg in "$@"; do
    case "$arg" in
        --details) show_details=true ;;
        --mark)    mark_checked=true ;;
        --reset)   reset_mode=true ;;
        --help|-h) echo "Usage: $0 [--details] [--mark] [--reset]"; exit 0 ;;
    esac
done

# Fetch latest from upstream
echo -e "${BOLD}=== G-Helper Upstream Check ===${NC}"
echo -e "${DIM}Fetching upstream...${NC}"
git -C "$UPSTREAM_DIR" fetch origin --quiet 2>/dev/null

# Get current upstream HEAD
upstream_head=$(git -C "$UPSTREAM_DIR" rev-parse HEAD)
upstream_head_short=$(git -C "$UPSTREAM_DIR" rev-parse --short HEAD)
upstream_head_date=$(git -C "$UPSTREAM_DIR" log -1 --format='%ai' HEAD | cut -d' ' -f1)

# Get last checked commit
if [[ -f "$STATE_FILE" ]]; then
    last_checked=$(cat "$STATE_FILE" | tr -d '[:space:]')
    last_checked_short=$(git -C "$UPSTREAM_DIR" rev-parse --short "$last_checked" 2>/dev/null || echo "$last_checked")
    last_checked_date=$(git -C "$UPSTREAM_DIR" log -1 --format='%ai' "$last_checked" 2>/dev/null | cut -d' ' -f1 || echo "unknown")
else
    last_checked=""
    last_checked_short="(never)"
    last_checked_date="(never)"
fi

echo -e "Last checked: ${CYAN}${last_checked_short}${NC} (${last_checked_date})"
echo -e "Upstream HEAD: ${CYAN}${upstream_head_short}${NC} (${upstream_head_date})"

# Reset mode — just save and exit
if $reset_mode; then
    echo "$upstream_head" > "$STATE_FILE"
    echo -e "${GREEN}Marked ${upstream_head_short} as last checked${NC}"
    exit 0
fi

# Count new commits
if [[ -z "$last_checked" ]]; then
    echo -e "${YELLOW}No previous check recorded. Use --reset to set baseline, or --mark after review.${NC}"
    commit_range="HEAD~20..HEAD"
    echo -e "Showing last 20 commits as preview:\n"
else
    # Check if last_checked is ancestor of HEAD
    if ! git -C "$UPSTREAM_DIR" merge-base --is-ancestor "$last_checked" HEAD 2>/dev/null; then
        echo -e "${RED}WARNING: Last checked commit is not an ancestor of HEAD (force push?)${NC}"
        echo -e "Use --reset to set new baseline."
        exit 1
    fi

    new_count=$(git -C "$UPSTREAM_DIR" rev-list --count "${last_checked}..HEAD")

    if [[ "$new_count" -eq 0 ]]; then
        echo -e "${GREEN}No new commits since last check.${NC}"
        exit 0
    fi

    echo -e "New commits: ${BOLD}${new_count}${NC}\n"
    commit_range="${last_checked}..HEAD"
fi

# Categorize commits
relevant=()
skipped=()

while IFS= read -r line; do
    hash=$(echo "$line" | cut -d' ' -f1)
    msg=$(echo "$line" | cut -d' ' -f2-)
    msg_lower=$(echo "$msg" | tr '[:upper:]' '[:lower:]')

    if echo "$msg_lower" | grep -qEi "$SKIP_PATTERNS"; then
        skipped+=("$line")
    elif echo "$msg_lower" | grep -qEi "$RELEVANT_PATTERNS"; then
        relevant+=("$line")
    elif echo "$msg_lower" | grep -qEi "merge|cleanup|version.bump|ui.tweak"; then
        skipped+=("$line")
    else
        # Unknown — mark as potentially relevant to be safe
        relevant+=("$line")
    fi
done < <(git -C "$UPSTREAM_DIR" log --oneline --no-merges "$commit_range")

# Display results
if [[ ${#relevant[@]} -gt 0 ]]; then
    echo -e "${GREEN}${BOLD}POTENTIALLY RELEVANT (${#relevant[@]}):${NC}"
    for line in "${relevant[@]}"; do
        hash=$(echo "$line" | cut -d' ' -f1)
        msg=$(echo "$line" | cut -d' ' -f2-)
        # Show files changed
        files=$(git -C "$UPSTREAM_DIR" show --stat --format="" "$hash" | sed 's/^ //' | head -5)
        echo -e "  ${CYAN}${hash}${NC} ${msg}"
        echo -e "  ${DIM}${files}${NC}\n"
    done

    # Show diffs if requested
    if $show_details; then
        echo -e "\n${BOLD}=== DIFFS FOR RELEVANT COMMITS ===${NC}\n"
        for line in "${relevant[@]}"; do
            hash=$(echo "$line" | cut -d' ' -f1)
            msg=$(echo "$line" | cut -d' ' -f2-)
            echo -e "${BOLD}── ${hash} ${msg} ──${NC}"
            git -C "$UPSTREAM_DIR" show --stat "$hash"
            echo ""
            git -C "$UPSTREAM_DIR" diff "${hash}~1..${hash}" -- '*.cs' 2>/dev/null || true
            echo -e "\n"
        done
    fi
else
    echo -e "${GREEN}POTENTIALLY RELEVANT: (none)${NC}"
fi

if [[ ${#skipped[@]} -gt 0 ]]; then
    echo -e "${DIM}SKIPPED — mouse/ally/xgm/crowdin/readme/cleanup (${#skipped[@]}):${NC}"
    for line in "${skipped[@]}"; do
        echo -e "  ${DIM}${line}${NC}"
    done
fi

# Mark as checked if requested
if $mark_checked; then
    echo ""
    echo "$upstream_head" > "$STATE_FILE"
    echo -e "${GREEN}Marked ${upstream_head_short} as last checked${NC}"
else
    echo -e "\n${YELLOW}Run with --mark to save this as checked, or --details to see diffs.${NC}"
fi
