#!/usr/bin/env bash

set -euo pipefail

ROOT_PATH=$(cd "$(dirname "$0")/.." && pwd)
temp_root=$(mktemp -d "${TMPDIR:-/tmp}/kusto-dev-release-test-XXXXXX")
trap 'rm -rf "$temp_root"' EXIT
mkdir -p "${temp_root}/bin" "${temp_root}/bundle"
touch "${temp_root}/bundle/checksums.txt"

fail() {
    echo "Error: $*" >&2
    exit 1
}

cat > "${temp_root}/bin/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_CALLS"
case "$1 $2" in
    "api repos/"*/releases/tags/*)
        if [[ "$MOCK_RELEASE" == "missing" ]]; then
            echo "gh: Not Found (HTTP 404)" >&2
            exit 1
        fi
        if [[ "$MOCK_RELEASE" == "unavailable" ]]; then
            echo "gh: Service Unavailable (HTTP 503)" >&2
            exit 1
        fi
        [[ "$MOCK_RELEASE" == "draft" ]] && echo true || echo false
        ;;
    "api repos/"*/git/ref/tags/*)
        if [[ "$*" == *".object.type"* ]]; then
            echo "$MOCK_TAG_TYPE"
        elif [[ "$MOCK_TAG_TYPE" == "tag" ]]; then
            echo annotated-tag-sha
        else
            echo "$MOCK_TAG_SHA"
        fi
        ;;
    "api repos/"*/git/tags/*)
        echo "$MOCK_TAG_SHA"
        ;;
    "release edit"|"release upload"|"release create")
        [[ "$MOCK_RELEASE" != "published" ]] || exit 99
        ;;
    *)
        echo "Unexpected gh call: $*" >&2
        exit 1
        ;;
esac
MOCK
chmod +x "${temp_root}/bin/gh"

export PATH="${temp_root}/bin:$PATH"
export GITHUB_SHA=source-commit GITHUB_REPOSITORY=owner/repo
export GITHUB_SERVER_URL=https://github.com GITHUB_RUN_NUMBER=2 GITHUB_RUN_ID=123
export MOCK_CALLS="${temp_root}/calls"
export MOCK_RELEASE=published MOCK_TAG_SHA="$GITHUB_SHA" MOCK_TAG_TYPE=commit

run_publish() {
    : > "$MOCK_CALLS"
    bash "${ROOT_PATH}/scripts/update-dev-release.sh" 1.2.3-pre.1.dev.1 "${temp_root}/bundle" 2>&1
}

for MOCK_TAG_TYPE in commit tag; do
    output=$(run_publish) || fail "A published release for the same commit must succeed."
    [[ "$output" == *"already published"* ]] || fail "Expected a notice about the existing release."
    if grep -Eq '^release (edit|upload|create)' "$MOCK_CALLS"; then
        fail "A published release must not be mutated."
    fi
done

MOCK_TAG_SHA=other-commit
if output=$(run_publish); then
    fail "A release targeting a different commit must fail."
fi
[[ "$output" == *"refusing to mutate an immutable version"* ]] || fail "Expected a commit collision error."
if grep -Eq '^release (edit|upload|create)' "$MOCK_CALLS"; then
    fail "A mismatched release must not be mutated."
fi

MOCK_TAG_TYPE=commit MOCK_TAG_SHA="$GITHUB_SHA" MOCK_RELEASE=draft
run_publish >/dev/null || fail "An existing draft must still support completion."
grep -q '^release edit ' "$MOCK_CALLS" || fail "Expected draft notes to be updated."
grep -q '^release upload ' "$MOCK_CALLS" || fail "Expected draft assets to be uploaded."

MOCK_RELEASE=missing
run_publish >/dev/null || fail "A missing release must still be created."
grep -q '^release create ' "$MOCK_CALLS" || fail "Expected a new release to be created."

MOCK_RELEASE=unavailable
if output=$(run_publish); then
    fail "A release lookup failure must not be treated as a missing release."
fi
[[ "$output" == *"Unable to inspect Dev release"* ]] || fail "Expected a release lookup error."
if grep -Eq '^release (edit|upload|create)' "$MOCK_CALLS"; then
    fail "A failed lookup must not trigger release mutations."
fi

awk '
    /- name: Publish next release state/ { in_step = 1 }
    in_step && /set -euo pipefail/ { in_script = 1 }
    in_script && /mkdir -p/ { exit }
    in_script { sub(/^          /, ""); print }
' "${ROOT_PATH}/.github/workflows/ci.yml" > "${temp_root}/state-guard.sh"
grep -q 'CURRENT_STATE' "${temp_root}/state-guard.sh" || fail "The release-state guard was not found."
echo 'echo STATE_UPDATE_ALLOWED' >> "${temp_root}/state-guard.sh"

cat > "${temp_root}/bin/git" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
    ls-remote)
        [[ "$MOCK_STATE" == "missing" ]] || echo "sha refs/heads/release-state"
        ;;
    fetch) ;;
    show) echo "$MOCK_STATE" ;;
    *) exit 1 ;;
esac
MOCK
cat > "${temp_root}/bin/jq" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == "-S -c ." ]] || exit 1
cat
MOCK
chmod +x "${temp_root}/bin/git" "${temp_root}/bin/jq"

export EXPECTED_STATE='{"devNumber":6}'
export MOCK_STATE
for MOCK_STATE in missing "$EXPECTED_STATE"; do
    output=$(bash "${temp_root}/state-guard.sh") || fail "A new state update must succeed."
    [[ "$output" == *"STATE_UPDATE_ALLOWED"* ]] || fail "An unchanged or missing state must allow advancement."
done

MOCK_STATE='{"devNumber":7}'
output=$(bash "${temp_root}/state-guard.sh") || fail "An already-advanced state must be a successful no-op."
[[ "$output" == *"leaving it unchanged"* ]] || fail "Expected a notice about advanced state."
[[ "$output" != *"STATE_UPDATE_ALLOWED"* ]] || fail "A rerun must not roll back advanced state."

echo "Dev release validation passed."
