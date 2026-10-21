#!/usr/bin/env bash
# Run only inside a disposable, network-disabled validation container.
set -euo pipefail
[[ -f /.dockerenv && ${KARDIA_OFFLINE_TEST:-} == 1 ]] || {
    echo 'Requires a disposable Docker container with KARDIA_OFFLINE_TEST=1.' >&2; exit 1;
}
source_dir=$(cd "$(dirname "$0")" && pwd)
test_dir=$(mktemp -d /tmp/kardia-installer-test.XXXXXXXX)
export KARDIA_TEST_DIR="$test_dir"
mkdir -p "$test_dir/bin" /etc/kardia
export PATH="$test_dir/bin:$PATH"
cat > "$test_dir/bin/az" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1 $2" >> "$KARDIA_TEST_DIR/azure-calls"
mkdir -p "$AZURE_CONFIG_DIR/nested"
printf 'offline-test-token' > "$AZURE_CONFIG_DIR/nested/token"
case "$1 $2" in
    'login --identity') ;;
    'keyvault secret') cat "$KARDIA_TEST_DIR/bundle.json" ;;
    'acr login') printf 'offline-test-token' ;;
    *) echo 'Unexpected Azure invocation.' >&2; exit 90 ;;
esac
MOCK
cat > "$test_dir/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
    info) ;;
    container) test -f "$KARDIA_TEST_DIR/existing-container" ;;
    login)
        cat > /dev/null
        mkdir -p "$DOCKER_CONFIG/nested"
        printf 'offline-test-token' > "$DOCKER_CONFIG/nested/token"
        ;;
    pull) ;;
    run) printf '%s\n' "$@" > "$KARDIA_TEST_DIR/docker-args" ;;
    *) echo 'Unexpected Docker invocation.' >&2; exit 91 ;;
esac
MOCK
chmod 0700 "$test_dir/bin/az" "$test_dir/bin/docker"
openssl req -x509 -newkey rsa:2048 -nodes -days 3 -subj /CN=Offline-Test-CA \
    -keyout "$test_dir/ca.key" -out "$test_dir/ca.crt" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj /CN=workflows.example.com \
    -keyout "$test_dir/server.key" -out "$test_dir/server.csr" >/dev/null 2>&1
printf '%s\n' 'subjectAltName=DNS:*.kardia.internal,DNS:workflows.example.com' \
    'basicConstraints=critical,CA:FALSE' 'extendedKeyUsage=serverAuth' > "$test_dir/extensions"
openssl x509 -req -in "$test_dir/server.csr" -CA "$test_dir/ca.crt" -CAkey "$test_dir/ca.key" \
    -CAcreateserial -days 3 -extfile "$test_dir/extensions" -out "$test_dir/server.crt" >/dev/null 2>&1

configure() {
    local role="$1"
    jq -n --arg role "$role" '{
        schemaVersion: 1, role: $role, registryName: "kardiaregistry",
        registryLoginServer: "kardiaregistry.azurecr.io",
        image: ("kardiaregistry.azurecr.io/kardia@sha256:" + ("a" * 64)),
        privateDnsSuffix: "kardia.internal",
        configurationSecretUri: ("https://kardia-config.vault.azure.net/secrets/kardia-" + $role + "/" + ("a" * 32))
    }' > /etc/kardia/role.json
    jq --rawfile cert "$test_dir/server.crt" --rawfile key "$test_dir/server.key" \
        --rawfile ca "$test_dir/ca.crt" --arg role "$role" '
        .tls.caPem = $ca |
        if $role == "agent" then . else .tls.certificatePem = $cert | .tls.privateKeyPem = $key end
    ' "$source_dir/$role.bundle.example.json" > "$test_dir/bundle.json"
    rm -f "$test_dir/existing-container" "$test_dir/docker-args" "$test_dir/azure-calls"
}
mutate_bundle() {
    jq "$1" "$test_dir/bundle.json" > "$test_dir/mutated.json"
    mv "$test_dir/mutated.json" "$test_dir/bundle.json"
}
expect_failure() {
    local description="$1" expected="$2"
    if bash "$source_dir/install-role.sh" > "$test_dir/output" 2>&1; then
        echo "FAIL: $description unexpectedly succeeded." >&2; exit 1
    fi
    grep -F "$expected" "$test_dir/output" >/dev/null || {
        echo "FAIL: $description did not report the expected error." >&2; cat "$test_dir/output" >&2; exit 1;
    }
    [[ ! -f $test_dir/docker-args ]]
    echo "PASS: $description"
}
expect_success() {
    local role="$1"
    bash "$source_dir/install-role.sh" > "$test_dir/output" 2>&1 || { cat "$test_dir/output" >&2; exit 1; }
    grep -Fx 'no-new-privileges:true' "$test_dir/docker-args" >/dev/null
    grep -Fx -- '--read-only' "$test_dir/docker-args" >/dev/null
    [[ -z $(find /run -maxdepth 1 -name 'kardia-azure.*' -o -name 'kardia-docker.*') ]]
    [[ -z $(find /etc/kardia/releases -name bundle.json) ]]
    echo "PASS: $role initial installation; credentials removed"
}

configure frontend
touch "$test_dir/existing-container"
expect_failure 'refuses replacing a live container' 'Refusing to replace'
[[ ! -f $test_dir/azure-calls ]]
configure api
jq '.image = "kardiaregistry.azurecr.io/kardia:latest"' /etc/kardia/role.json > "$test_dir/profile"
cp "$test_dir/profile" /etc/kardia/role.json
expect_failure 'refuses mutable image tags before identity login' 'pinned by digest'
[[ ! -f $test_dir/azure-calls ]]
configure api
mutate_bundle '.settings.Authentication.Mode = "Development"'
expect_failure 'refuses development server authentication' 'require Entra'
configure api
mutate_bundle '.settings.Kestrel.Endpoints.PrivateHttps.Url = "http://0.0.0.0:8443"'
expect_failure 'refuses plaintext private server endpoint' 'private Kestrel TLS'
configure frontend
mutate_bundle '.frontend.publicHostname = "wrong.example.com"'
expect_failure 'refuses certificate hostname mismatch' 'cover this role hostname'
configure frontend
mutate_bundle '.frontend.apiOrigins = ["https://only-one.kardia.internal:8443"]'
expect_failure 'refuses a single backend origin' 'at least two HTTPS origins'
configure agent
mutate_bundle '.tls.privateKeyPem = "must-not-be-shared"'
expect_failure 'refuses TLS server keys on workers' 'must not receive'
configure agent
mutate_bundle '.settings.Agent.Engines[0] = "http://controller.kardia.internal:8444"'
expect_failure 'refuses plaintext worker engines' 'multiple HTTPS engines'
configure agent
mutate_bundle '.settings.Agent.Id = "11111111-1111-4111-8111-111111111111"'
expect_failure 'refuses shared worker UUID in role settings' 'no shared UUID'
for role in frontend api controller agent; do
    configure "$role"
    expect_success "$role"
done
[[ ! $(cat "$test_dir/docker-args") =~ target=/tls ]]
identity=$(cat /var/lib/kardia/agent/identity)
configure agent
expect_success agent
[[ $(cat /var/lib/kardia/agent/identity) == "$identity" ]]
echo 'PASS: agent identity survives installation retry'
rm -f /var/lib/kardia/agent/identity
touch /var/lib/kardia/agent/previous-state
configure agent
expect_failure 'refuses regenerating identity alongside retained state' 'Missing agent UUID'
[[ -z $(find /run -maxdepth 1 -name 'kardia-azure.*' -o -name 'kardia-docker.*') ]]
[[ -z $(find /etc/kardia/releases -name bundle.json) ]]
echo 'PASS: failure paths remove credential caches and raw bundles'
