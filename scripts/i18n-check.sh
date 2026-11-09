#!/usr/bin/env bash
# i18n-check.sh - Validate translation key consistency across all language files
#
# English.cs is the canonical schema. This script enforces that:
#   A) every other language defines exactly the same keys (no missing, no drift)
#   B) every key defined in English is actually consumed by code
#   C) no language file contains keys that don't exist in English
#   E) no word mixes alphabets (looks right, breaks search and screen readers)
#
# Why this exists:
#   The Labels.Get() method silently falls back to English when a key is missing
#   in the user's locale. That makes drift invisible at runtime, so without this
#   guard a missing translation can ship undetected for months. This script makes
#   the drift loud at PR time.
#
# Usage:
#   ./scripts/i18n-check.sh              # Run all audits, exit 1 on ANY finding
#   ./scripts/i18n-check.sh --summary    # Single-line counts, no detail
#   ./scripts/i18n-check.sh --json       # Machine-readable output for CI
#   ./scripts/i18n-check.sh --quiet      # Only print errors, suppress headers
#   ./scripts/i18n-check.sh --help       # Show this help
#
# Exit codes:
#   0 - all audits pass
#   1 - at least one audit found an issue
#   2 - script setup error (missing files, etc.)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LANG_DIR="$REPO_ROOT/src/I18n/Languages"
SRC_DIR="$REPO_ROOT/src"
ENGLISH_FILE="$LANG_DIR/English.cs"
LABELS_FILE="$REPO_ROOT/src/I18n/Labels.cs"

# Colors (only when stdout is a TTY)
if [[ -t 1 ]]; then
    RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'
    CYAN=$'\033[0;36m'; DIM=$'\033[2m'; BOLD=$'\033[1m'; NC=$'\033[0m'
else
    RED=''; GREEN=''; YELLOW=''; CYAN=''; DIM=''; BOLD=''; NC=''
fi

# Indirect-lookup allowlist:
#   These prefixes are emitted as string literals from C# code (e.g.
#   SystemInfoCollector.cs builds entries with "sysinfo_xxx" labels that are
#   later resolved via Labels.Get(entry.LabelKey)). A naive "find Labels.Get"
#   scan would falsely flag them as unused, so we treat any key matching these
#   prefixes as "used" if a string literal of that key appears anywhere in src/.
#   - "sysinfo_": SystemInfoCollector emits Label keys as bare string literals
#     and resolves them via Labels.Get(entry.LabelKey) later.
#   - "bind_", "btn_", "ally_grp_", "controller_mode_": ROG Ally controller
#     binding catalog (BindingGroups.cs) stores i18n keys in tuple Display
#     fields; HandheldWindow looks them up via Labels.Get(entry.Display) at
#     dropdown population time.
#   - "sysfiles_name_": Install/Installer.cs stores each managed file's display
#     key as a ManagedFile.NameKey literal, resolved via Labels.Get(f.NameKey).
#   - "mkey_": Input/MKeyControl.cs stores firmware-opcode label keys as string
#     literals in its Choices table, resolved via Labels.Get(key) in OpcodeChoices.
#   - "gpu_block_unavail_": Gpu/Block/GpuBlockCapability.cs returns the reason
#     the block is unavailable as a label key, resolved via Labels.Get(reason).
#   - "gpu_block_cold_": Gpu/Block/DgpuPowerMonitor.cs returns the reason a blocked
#     dGPU stayed awake as a label key, resolved via Labels.Get(reasonKey).
INDIRECT_PREFIXES=("sysinfo_" "bind_" "btn_" "ally_grp_" "controller_mode_" "sysfiles_name_" "mkey_" "gpu_block_unavail_" "gpu_block_cold_")

# ----- argument parsing -----

mode="full"      # full | summary | json | quiet
for arg in "$@"; do
    case "$arg" in
        --summary) mode="summary" ;;
        --json)    mode="json" ;;
        --quiet)   mode="quiet" ;;
        --help|-h)
            sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "Unknown option: $arg" >&2
            echo "Run '$0 --help' for usage." >&2
            exit 2
            ;;
    esac
done

if [[ ! -d "$LANG_DIR" ]]; then
    echo "ERROR: Language directory not found: $LANG_DIR" >&2
    exit 2
fi
if [[ ! -f "$ENGLISH_FILE" ]]; then
    echo "ERROR: English.cs not found: $ENGLISH_FILE" >&2
    exit 2
fi
if [[ ! -f "$LABELS_FILE" ]]; then
    echo "ERROR: Labels.cs not found: $LABELS_FILE" >&2
    exit 2
fi

# ----- helpers -----

# Extract translation keys from a language file.
# Matches lines like:    ["some_key"] = "value",
extract_keys() {
    local file="$1"
    grep -oE '\["[a-zA-Z0-9_]+"\]' "$file" | sed 's/^\["//;s/"\]$//' | sort -u
}

# Collect every key referenced from C#/XAML code (direct + indirect).
collect_used_keys() {
    {
        # Direct usages - capture the key from Labels.Get/Format calls.
        # Capture every quoted token on lines containing these calls (covers
        # ternary expressions like Labels.Format(cond ? "a" : "b", ...) which
        # produce two candidates per line).
        grep -rhE 'Labels\.(Get|Format)\(' \
            --include='*.cs' --include='*.axaml' \
            "$SRC_DIR" 2>/dev/null \
            | grep -oE '"[a-zA-Z0-9_]+"' \
            | sed 's/"//g'

        # Indirect usages - any literal matching a known dynamic prefix
        # anywhere in source (excluding the language dictionaries themselves).
        for prefix in "${INDIRECT_PREFIXES[@]}"; do
            grep -rhoE "\"${prefix}[a-zA-Z0-9_]+\"" \
                --include='*.cs' --include='*.axaml' \
                --exclude-dir='Languages' \
                "$SRC_DIR" 2>/dev/null \
                | sed 's/"//g'
        done
    } | sort -u
}

declare -A LOADER_SET=()

is_loader_code() { [[ -n "${LOADER_SET[$1]:-}" ]]; }

# Parse the LanguageLoaders dictionary keys from Labels.cs:
#   { "xx", () => Languages.Foo.Translations },  ->  xx
load_loader_codes() {
    while IFS= read -r code; do
        [[ -n "$code" ]] && LOADER_SET["$code"]=1
    done < <(grep -E '=>[[:space:]]*Languages\.' "$LABELS_FILE" \
                 | sed -E 's/.*\{[[:space:]]*"([^"]+)".*/\1/')
}

# Mirror of DetectLocale(): "en_US.UTF-8" -> "en", "zh_CN.UTF-8" -> "zh-cn".
detect_locale() {
    local input="$1"
    [[ -z "$input" ]] && { printf 'en'; return; }
    local code="${input%%.*}"            # strip ".UTF-8"
    local full="${code//_/-}"; full="${full,,}"
    if is_loader_code "$full"; then printf '%s' "$full"; return; fi
    local lang_only="${code%%_*}"; lang_only="${lang_only,,}"
    if is_loader_code "$lang_only"; then printf '%s' "$lang_only"; return; fi
    # Special cases - keep identical to DetectLocale():
    if [[ "$lang_only" == "no" ]] && is_loader_code "nb"; then printf 'nb'; return; fi
    if [[ "$lang_only" == "tl" ]] && is_loader_code "fil"; then printf 'fil'; return; fi
    printf 'en'
}

# Representative real-world POSIX locales -> expected detected code.
# Every registered language MUST appear as an expected code in at least one row,
# otherwise it is unreachable by auto-detection and Audit D fails. Add a row
# (using the actual glibc locale name) whenever you add a language.
DETECT_FIXTURES=(
    "en_US.UTF-8|en"    "ar_SA.UTF-8|ar"    "cs_CZ.UTF-8|cs"    "da_DK.UTF-8|da"
    "de_DE.UTF-8|de"    "el_GR.UTF-8|el"    "es_ES.UTF-8|es"    "fi_FI.UTF-8|fi"
    "fr_FR.UTF-8|fr"    "hu_HU.UTF-8|hu"    "id_ID.UTF-8|id"    "it_IT.UTF-8|it"
    "ja_JP.UTF-8|ja"    "mk_MK.UTF-8|mk"    "ko_KR.UTF-8|ko"    "nl_NL.UTF-8|nl"
    "nb_NO.UTF-8|nb"    "no_NO.UTF-8|nb"    "pl_PL.UTF-8|pl"    "pt_BR.UTF-8|pt-br"
    "ro_RO.UTF-8|ro"    "ru_RU.UTF-8|ru"    "sr_RS.UTF-8|sr"    "sv_SE.UTF-8|sv"
    "th_TH.UTF-8|th"    "tr_TR.UTF-8|tr"    "uk_UA.UTF-8|uk"    "vi_VN.UTF-8|vi"
    "zh_CN.UTF-8|zh-cn" "zh_TW.UTF-8|zh-tw"
    "ne_NP.UTF-8|ne"    "ms_MY.UTF-8|ms"    "bn_BD.UTF-8|bn"    "bn_IN.UTF-8|bn"
    "sk_SK.UTF-8|sk"    "lt_LT.UTF-8|lt"    "lv_LV.UTF-8|lv"    "sl_SI.UTF-8|sl"
    "hi_IN.UTF-8|hi"    "fil_PH.UTF-8|fil"  "tl_PH.UTF-8|fil"
)

# ----- gather data -----

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

EN_KEYS_FILE="$WORK_DIR/en.keys"
USED_KEYS_FILE="$WORK_DIR/used.keys"

extract_keys "$ENGLISH_FILE" > "$EN_KEYS_FILE"
collect_used_keys > "$USED_KEYS_FILE"

en_count=$(wc -l < "$EN_KEYS_FILE" | tr -d ' ')
used_count=$(wc -l < "$USED_KEYS_FILE" | tr -d ' ')
other_lang_count=$(( $(ls "$LANG_DIR"/*.cs 2>/dev/null | wc -l) - 1 ))

# ----- Audit A: missing keys (per language) -----
# For each non-English language file, write a per-language report file
# containing the keys that are in English but missing locally. We use files
# instead of arrays-with-newlines to keep the data flow simple and reliable.

MISSING_DIR="$WORK_DIR/missing"
mkdir -p "$MISSING_DIR"

total_missing=0
missing_files=()

for file in "$LANG_DIR"/*.cs; do
    name=$(basename "$file")
    if [[ "$name" == "English.cs" ]]; then
        continue
    fi

    lang_keys_file="$WORK_DIR/${name}.keys"
    extract_keys "$file" > "$lang_keys_file"

    out="$MISSING_DIR/$name"
    comm -23 "$EN_KEYS_FILE" "$lang_keys_file" > "$out"

    if [[ -s "$out" ]]; then
        count=$(wc -l < "$out" | tr -d ' ')
        total_missing=$((total_missing + count))
        missing_files+=("$name:$count")
    else
        rm -f "$out"
    fi
done

# ----- Audit B: dead keys (defined in English, never used in code) -----

DEAD_FILE="$WORK_DIR/dead.keys"
comm -23 "$EN_KEYS_FILE" "$USED_KEYS_FILE" > "$DEAD_FILE"
dead_count=0
if [[ -s "$DEAD_FILE" ]]; then
    dead_count=$(wc -l < "$DEAD_FILE" | tr -d ' ')
fi

# ----- Audit C: drifting keys (in lang file but not in English) -----

DRIFT_DIR="$WORK_DIR/drift"
mkdir -p "$DRIFT_DIR"

total_drift=0
drift_files=()

for file in "$LANG_DIR"/*.cs; do
    name=$(basename "$file")
    if [[ "$name" == "English.cs" ]]; then
        continue
    fi

    lang_keys_file="$WORK_DIR/${name}.keys"
    out="$DRIFT_DIR/$name"
    comm -13 "$EN_KEYS_FILE" "$lang_keys_file" > "$out"

    if [[ -s "$out" ]]; then
        count=$(wc -l < "$out" | tr -d ' ')
        total_drift=$((total_drift + count))
        drift_files+=("$name:$count")
    else
        rm -f "$out"
    fi
done

load_loader_codes

declare -A FIXTURE_EXPECTED=()
detect_mismatch=0
detect_unregistered=0
mismatch_list=()
unreg_list=()

for row in "${DETECT_FIXTURES[@]}"; do
    locale="${row%%|*}"
    expected="${row##*|}"
    FIXTURE_EXPECTED["$expected"]=1

    got="$(detect_locale "$locale")"
    if [[ "$got" != "$expected" ]]; then
        detect_mismatch=$((detect_mismatch + 1))
        mismatch_list+=("$locale -> $got (expected $expected)")
    fi
    if ! is_loader_code "$expected"; then
        detect_unregistered=$((detect_unregistered + 1))
        unreg_list+=("$locale expects '$expected' - not in LanguageLoaders")
    fi
done

detect_uncovered=0
uncovered_list=()
for code in "${!LOADER_SET[@]}"; do
    if [[ -z "${FIXTURE_EXPECTED[$code]:-}" ]]; then
        detect_uncovered=$((detect_uncovered + 1))
        uncovered_list+=("$code")
    fi
done

# Sort the unordered associative-array output for deterministic reports.
if ((${#uncovered_list[@]})); then
    mapfile -t uncovered_list < <(printf '%s\n' "${uncovered_list[@]}" | sort)
fi
if ((${#mismatch_list[@]})); then
    mapfile -t mismatch_list < <(printf '%s\n' "${mismatch_list[@]}" | sort)
fi
if ((${#unreg_list[@]})); then
    mapfile -t unreg_list < <(printf '%s\n' "${unreg_list[@]}" | sort)
fi

loader_count=${#LOADER_SET[@]}
detect_fail=$((detect_mismatch + detect_unregistered + detect_uncovered))

# ----- Audit E: mixed alphabets -----
# Words mixing Latin/Cyrillic/Greek, or Cyrillic in other languages; \n escapes blanked.

CYRILLIC_LANGS=" Russian.cs Ukrainian.cs Serbian.cs Macedonian.cs "
WORD='[\p{L}\p{M}]*'
MIX_PAT="$WORD(?:[A-Za-z]$WORD[\p{Cyrillic}\p{Greek}]|[\p{Cyrillic}\p{Greek}]$WORD[A-Za-z]|\p{Cyrillic}$WORD\p{Greek}|\p{Greek}$WORD\p{Cyrillic})$WORD"
MIXED_FILE="$WORK_DIR/mixed"
: > "$MIXED_FILE"
for file in "$LANG_DIR"/*.cs; do
    name=$(basename "$file")
    pat="$MIX_PAT"
    [[ "$CYRILLIC_LANGS" == *" $name "* ]] || pat="$MIX_PAT|$WORD\p{Cyrillic}$WORD"
    sed -E 's/\\[nrt"\\]/ /g' "$file" | LC_ALL=C.UTF-8 grep -noP "$pat" \
        | sed "s|^|$name:|" >> "$MIXED_FILE" || true
done
mixed_count=$(grep -c . "$MIXED_FILE" || true)

# ----- output -----

exit_code=0
if [[ $total_missing -gt 0 || $dead_count -gt 0 || $total_drift -gt 0 || $detect_fail -gt 0 || $mixed_count -gt 0 ]]; then
    exit_code=1
fi

if [[ "$mode" == "json" ]]; then
    printf '{'
    printf '"english_keys":%d,' "$en_count"
    printf '"used_keys":%d,' "$used_count"
    printf '"total_missing":%d,' "$total_missing"
    printf '"dead_count":%d,' "$dead_count"
    printf '"total_drift":%d,' "$total_drift"
    printf '"languages_with_missing":%d,' "${#missing_files[@]}"
    printf '"languages_with_drift":%d,' "${#drift_files[@]}"
    printf '"registered_languages":%d,' "$loader_count"
    printf '"detect_mismatch":%d,' "$detect_mismatch"
    printf '"detect_unregistered":%d,' "$detect_unregistered"
    printf '"detect_uncovered":%d,' "$detect_uncovered"
    printf '"mixed_scripts":%d,' "$mixed_count"
    printf '"exit_code":%d' "$exit_code"
    printf '}\n'
    exit "$exit_code"
fi

if [[ "$mode" == "summary" ]]; then
    if [[ $exit_code -eq 0 ]]; then
        echo "${GREEN}i18n OK${NC} - $en_count keys, $used_count used, 0 missing, 0 dead, 0 drift, $loader_count langs auto-detectable"
    else
        echo "${RED}i18n FAIL${NC} - $en_count keys | $total_missing missing across ${#missing_files[@]} langs | $dead_count dead | $total_drift drift across ${#drift_files[@]} langs | detect: $detect_mismatch mismatch / $detect_unregistered unregistered / $detect_uncovered uncovered | $mixed_count mixed-script"
    fi
    exit "$exit_code"
fi

# Full / quiet output

print_full() {
    [[ "$mode" == "full" ]] && echo "$@"
    return 0
}

print_full "${BOLD}=== i18n consistency check ===${NC}"
print_full "${DIM}English.cs:${NC} $en_count keys (canonical schema)"
print_full "${DIM}Code references:${NC} $used_count unique keys used"
print_full ""

# ---- Audit A output ----
print_full "${BOLD}[A] Missing keys${NC} ${DIM}(in English.cs but absent in other langs)${NC}"
if [[ $total_missing -eq 0 ]]; then
    print_full "  ${GREEN}✓ All $other_lang_count non-English files complete${NC}"
else
    for entry in "${missing_files[@]}"; do
        name="${entry%%:*}"
        count="${entry##*:}"
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}MISSING${NC} $name: $count keys"
        else
            echo "  ${RED}✗${NC} ${BOLD}$name${NC} - $count missing"
            while IFS= read -r k; do
                [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
            done < "$MISSING_DIR/$name"
        fi
    done
fi
print_full ""

# ---- Audit B output ----
print_full "${BOLD}[B] Dead keys${NC} ${DIM}(defined in English but never used in code)${NC}"
if [[ $dead_count -eq 0 ]]; then
    print_full "  ${GREEN}✓ All English keys are referenced by code${NC}"
else
    if [[ "$mode" == "quiet" ]]; then
        echo "${RED}DEAD${NC} English.cs: $dead_count keys"
    else
        echo "  ${RED}✗${NC} ${BOLD}English.cs${NC} - $dead_count dead keys:"
        while IFS= read -r k; do
            [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
        done < "$DEAD_FILE"
        print_full "  ${DIM}(also present in $other_lang_count other lang files - remove from all)${NC}"
    fi
fi
print_full ""

# ---- Audit C output ----
print_full "${BOLD}[C] Drifting keys${NC} ${DIM}(in lang file but not in English.cs)${NC}"
if [[ $total_drift -eq 0 ]]; then
    print_full "  ${GREEN}✓ No drift across $other_lang_count non-English files${NC}"
else
    for entry in "${drift_files[@]}"; do
        name="${entry%%:*}"
        count="${entry##*:}"
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}DRIFT${NC} $name: $count keys"
        else
            echo "  ${RED}✗${NC} ${BOLD}$name${NC} - $count drifting"
            while IFS= read -r k; do
                [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
            done < "$DRIFT_DIR/$name"
        fi
    done
fi
print_full ""

print_full "${BOLD}[D] DetectLocale coverage${NC} ${DIM}(every registered language is auto-detectable)${NC}"
if [[ $detect_fail -eq 0 ]]; then
    print_full "  ${GREEN}✓ All $loader_count registered languages reachable via DetectLocale (${#DETECT_FIXTURES[@]} locale fixtures)${NC}"
else
    if [[ $detect_uncovered -gt 0 ]]; then
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}DETECT-UNCOVERED${NC} $detect_uncovered langs"
        else
            echo "  ${RED}✗${NC} ${BOLD}$detect_uncovered registered language(s) unreachable by auto-detect${NC} ${DIM}(add a DETECT_FIXTURES row)${NC}"
            for c in "${uncovered_list[@]}"; do
                echo "      ${DIM}- $c${NC}"
            done
        fi
    fi
    if [[ $detect_mismatch -gt 0 ]]; then
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}DETECT-MISMATCH${NC} $detect_mismatch locales"
        else
            echo "  ${RED}✗${NC} ${BOLD}$detect_mismatch locale(s) resolve to the wrong language${NC}"
            for m in "${mismatch_list[@]}"; do
                echo "      ${DIM}- $m${NC}"
            done
        fi
    fi
    if [[ $detect_unregistered -gt 0 ]]; then
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}DETECT-UNREGISTERED${NC} $detect_unregistered fixtures"
        else
            echo "  ${RED}✗${NC} ${BOLD}$detect_unregistered fixture(s) expect an unregistered code${NC}"
            for u in "${unreg_list[@]}"; do
                echo "      ${DIM}- $u${NC}"
            done
        fi
    fi
fi
print_full ""

print_full "${BOLD}[E] Mixed alphabets${NC} ${DIM}(one word typed in two alphabets)${NC}"
if [[ $mixed_count -eq 0 ]]; then
    print_full "  ${GREEN}✓ No mixed-alphabet words${NC}"
elif [[ "$mode" == "quiet" ]]; then
    echo "${RED}MIXED${NC} $mixed_count words"
else
    echo "  ${RED}✗${NC} ${BOLD}$mixed_count mixed-alphabet word(s)${NC} ${DIM}(file:line:word)${NC}"
    while IFS= read -r m; do
        echo "      ${DIM}- $m${NC}"
    done < "$MIXED_FILE"
fi
print_full ""

# ---- Summary footer ----
if [[ $exit_code -eq 0 ]]; then
    print_full "${GREEN}${BOLD}✓ All audits passed${NC}"
else
    if [[ "$mode" == "quiet" ]]; then
        echo "${RED}FAIL${NC} $total_missing missing | $dead_count dead | $total_drift drift | $detect_fail detect | $mixed_count mixed"
    else
        print_full "${RED}${BOLD}✗ i18n drift detected${NC} - $total_missing missing across ${#missing_files[@]} langs | $dead_count dead | $total_drift drift across ${#drift_files[@]} langs | $detect_fail DetectLocale issue(s) | $mixed_count mixed-alphabet word(s)"
    fi
fi

exit "$exit_code"
