#!/usr/bin/env bash

set -euo pipefail

ROOT_PATH=$(cd "$(dirname "$0")/.." && pwd)
INSTALLER_PATH="${ROOT_PATH}/scripts/install/install-kusto-cli.sh"

# Load installer helpers without performing an installation.
. "$INSTALLER_PATH" --no-execute

fail() {
    echo "Error: $*" >&2
    exit 1
}

assert_contains() {
    local value="$1" expected="$2"
    [[ "$value" == *"$expected"* ]] ||
        fail "Expected '$value' to contain '$expected'."
}

temp_root=$(mktemp -d "${TMPDIR:-/tmp}/kusto-unix-installer-test-XXXXXX")
trap 'rm -rf "$temp_root"' EXIT
mock_bin="${temp_root}/bin"
empty_bin="${temp_root}/empty"
mkdir -p "$mock_bin" "$empty_bin"

cat > "${mock_bin}/cosign" <<'MOCK'
#!/usr/bin/env bash
printf 'GitVersion: v%s\n' "${MOCK_COSIGN_VERSION}"
MOCK
chmod +x "${mock_bin}/cosign"

if output=$(
    PATH="${empty_bin}:/usr/bin:/bin"
    export PATH
    assert_cosign_available 2>&1
); then
    fail "A missing cosign command should fail validation."
fi
assert_contains "$output" "cosign ${MINIMUM_COSIGN_VERSION} or newer is required"

if output=$(
    PATH="${mock_bin}:/usr/bin:/bin"
    MOCK_COSIGN_VERSION="2.3.0"
    export PATH MOCK_COSIGN_VERSION
    assert_cosign_available 2>&1
); then
    fail "An outdated cosign command should fail validation."
fi
assert_contains "$output" "cosign ${MINIMUM_COSIGN_VERSION} or newer is required, but 2.3.0 was found"

saved_path="$PATH"
PATH="${mock_bin}:/usr/bin:/bin"
export PATH

MOCK_COSIGN_VERSION="2.4.0"
export MOCK_COSIGN_VERSION
assert_cosign_available
[[ "$COSIGN_VERSION" == "2.4.0" ]] ||
    fail "Expected cosign 2.4.0 to pass validation."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --new-bundle-format "* ]] ||
    fail "Cosign 2.x must use --new-bundle-format."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --type slsaprovenance1 "* ]] ||
    fail "Cosign 2.x must select the SLSA v1 predicate."

MOCK_COSIGN_VERSION="3.1.3"
export MOCK_COSIGN_VERSION
assert_cosign_available
[[ "$COSIGN_VERSION" == "3.1.3" ]] ||
    fail "Expected cosign 3.1.3 to pass validation."
[[ " ${COSIGN_VERIFY_ARGS[*]} " != *" --new-bundle-format "* ]] ||
    fail "Cosign 3.x must not use the deprecated --new-bundle-format flag."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --type slsaprovenance1 "* ]] ||
    fail "Cosign 3.x must select the SLSA v1 predicate."

[[ "$(get_azure_cli_install_url osx)" == "https://learn.microsoft.com/cli/azure/install-azure-cli-macos" ]] ||
    fail "The macOS Azure CLI documentation URL is incorrect."
[[ "$(get_azure_cli_install_url linux)" == "https://learn.microsoft.com/cli/azure/install-azure-cli-linux" ]] ||
    fail "The Linux Azure CLI documentation URL is incorrect."

cat > "${mock_bin}/az" <<'MOCK'
#!/usr/bin/env bash
exit 0
MOCK
chmod +x "${mock_bin}/az"

PATH="$mock_bin"
[[ -z "$(print_azure_cli_guidance osx)" ]] ||
    fail "Azure CLI guidance should not be printed when az is available."

PATH="$empty_bin"
guidance=$(print_azure_cli_guidance linux)
assert_contains "$guidance" "Azure CLI was not found."
assert_contains "$guidance" "https://learn.microsoft.com/cli/azure/install-azure-cli-linux"

PATH="$saved_path"
export PATH

valid_payload=$(printf '%s' '{"_type":"https://in-toto.io/Statement/v1","predicateType":"https://slsa.dev/provenance/v1"}' |
    base64 |
    tr -d '\r\n')
invalid_payload=$(printf '%s' '{"_type":"https://in-toto.io/Statement/v1","predicateType":"https://example.com/custom"}' |
    base64 |
    tr -d '\r\n')
printf '{"dsseEnvelope":{"payload":"%s"}}\n' "$valid_payload" > "${temp_root}/valid-bundle.json"
printf '{"dsseEnvelope":{"payload":"%s"}}\n' "$invalid_payload" > "${temp_root}/invalid-bundle.json"

is_slsa_v1_provenance_bundle "${temp_root}/valid-bundle.json" ||
    fail "A signed SLSA v1 predicate should be accepted."
if is_slsa_v1_provenance_bundle "${temp_root}/invalid-bundle.json"; then
    fail "A non-SLSA predicate should be rejected."
fi

macos_payload="${temp_root}/macos-payload"
mkdir -p "${macos_payload}/native"
macos_files=(kusto libSkiaSharp.dylib libHarfBuzzSharp.dylib libsodium.dylib native/helper LICENSE)
for file in "${macos_files[@]}"; do
    if [[ "$file" == LICENSE ]]; then
        printf 'license\n' > "${macos_payload}/${file}"
    else
        printf '\317\372\355\376native-test' > "${macos_payload}/${file}"
    fi
done
codesign_calls="${temp_root}/codesign-calls.txt"
MOCK_SIGNATURE_MODE=valid
invoke_macos_codesign() {
    local argument last_argument=""
    for argument in "$@"; do last_argument="$argument"; done
    if [[ "$1" == --verify ]]; then
        [[ "$*" == *"--all-architectures"* &&
           "$*" == *"anchor apple generic"* &&
           "$*" == *"1.2.840.113635.100.6.2.6"* &&
           "$*" == *"1.2.840.113635.100.6.1.13"* &&
           "$*" == *"subject.OU] = \"${MACOS_SIGNING_TEAM_ID}\""* ]] ||
            fail "The codesign requirement must pin Apple, Developer ID Application, and our team for all architectures."
        printf '%s\n' "$last_argument" >> "$codesign_calls"
        case "$MOCK_SIGNATURE_MODE" in unsigned|tampered|wrong-team|wrong-certificate-type) return 1 ;; esac
    else
        if [[ "$MOCK_SIGNATURE_MODE" != no-runtime ]]; then
            echo 'CodeDirectory v=20500 flags=0x10000(runtime)'
        fi
        if [[ "$MOCK_SIGNATURE_MODE" != no-timestamp ]]; then
            echo 'Timestamp=Oct 4, 2026 at 12:00:00'
        fi
    fi
}

assert_macos_payload_signatures "$macos_payload" "${macos_files[@]}"
[[ "$(wc -l < "$codesign_calls" | tr -d ' ')" == 5 ]] ||
    fail "Every Mach-O file, including extensionless helpers, must be verified."
grep -Fq '/native/helper' "$codesign_calls" ||
    fail "Nested Mach-O helpers must not bypass signature verification."
if grep -Fq '/LICENSE' "$codesign_calls"; then
    fail "Non-native documents should not require code signatures."
fi

for mode in unsigned tampered wrong-team wrong-certificate-type no-runtime no-timestamp; do
    MOCK_SIGNATURE_MODE="$mode"
    if output=$(assert_macos_payload_signatures "$macos_payload" "${macos_files[@]}" 2>&1); then
        fail "A macOS payload with '$mode' should fail signature verification."
    fi
    assert_contains "$output" "Error:"
done

MOCK_SIGNATURE_MODE=valid
printf 'corrupt-library' > "${macos_payload}/future.dylib"
if output=$(assert_macos_payload_signatures "$macos_payload" "${macos_files[@]}" future.dylib 2>&1); then
    fail "Corrupt optional native libraries must not bypass signature verification."
fi
assert_contains "$output" "not a valid Mach-O payload"
rm -f "${macos_payload}/libsodium.dylib"
if output=$(assert_macos_payload_signatures "$macos_payload" "${macos_files[@]}" 2>&1); then
    fail "Missing macOS native sidecars should fail before execution."
fi
assert_contains "$output" "libsodium.dylib"

echo "Unix installer validation passed."
