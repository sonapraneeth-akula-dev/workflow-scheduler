#!/usr/bin/env bash
set -euo pipefail
umask 077
die() { printf '%s\n' "$*" >&2; exit 1; }
[[ $(id -u) == 0 ]] || die 'Run as root on the intended Kardia VM.'
for command in docker az jq curl openssl; do command -v "$command" >/dev/null || die "Missing required tool: $command"; done
docker info --format '{{.ServerVersion}}' >/dev/null
profile=/etc/kardia/role.json
jq -e '.schemaVersion == 1' "$profile" >/dev/null || die 'Invalid role profile schema.'
role=$(jq -er '.role' "$profile")
case "$role" in frontend|api|controller|agent) ;; *) echo 'Invalid role.' >&2; exit 1 ;; esac
name="kardia-$role"
if docker container inspect "$name" >/dev/null 2>&1; then
    echo "Refusing to replace $name. Use the drain-aware rollout runbook first." >&2
    exit 1
fi
registry=$(jq -er '.registryName' "$profile")
login_server=$(jq -er '.registryLoginServer' "$profile")
image=$(jq -er '.image' "$profile")
secret=$(jq -er '.configurationSecretUri' "$profile")
dns=$(jq -er '.privateDnsSuffix' "$profile")
[[ $registry =~ ^[a-zA-Z0-9]{5,50}$ && $login_server =~ ^[a-zA-Z0-9.-]+\.azurecr\.io$ ]] || die 'Invalid Azure Public Cloud registry.'
[[ $image == "$login_server/"* && $image =~ ^[a-zA-Z0-9./_-]+@sha256:[a-f0-9]{64}$ ]] || die 'Image must be pinned by digest in the approved registry.'
[[ $secret =~ ^https://[a-zA-Z0-9-]+\.vault\.azure\.net/secrets/[a-zA-Z0-9-]+/[a-f0-9]{32}$ ]] || die 'Configuration requires an HTTPS versioned Key Vault secret URI.'
[[ $dns =~ ^[a-zA-Z0-9][a-zA-Z0-9.-]+[a-zA-Z0-9]$ ]] || die 'Invalid private DNS suffix.'

install -d -m 0700 /etc/kardia/releases
release=$(mktemp -d /etc/kardia/releases/initial.XXXXXXXX)
bundle="$release/bundle.json"
AZURE_CONFIG_DIR=
DOCKER_CONFIG=
cleanup() {
    local directory
    rm -f "$bundle"
    for directory in "$AZURE_CONFIG_DIR" "$DOCKER_CONFIG"; do
        [[ -n $directory ]] || continue
        [[ $directory =~ ^/run/kardia-(azure|docker)\.[a-zA-Z0-9]{8}$ ]] ||
            die 'Refusing credential-cache cleanup outside the invocation directory.'
        find "$directory" -depth -delete
    done
}
trap cleanup EXIT
export AZURE_CONFIG_DIR DOCKER_CONFIG
AZURE_CONFIG_DIR=$(mktemp -d /run/kardia-azure.XXXXXXXX)
DOCKER_CONFIG=$(mktemp -d /run/kardia-docker.XXXXXXXX)
az login --identity --allow-no-subscriptions --output none
az keyvault secret show --id "$secret" --query value --output tsv > "$bundle"
jq -e '.schemaVersion == 1 and (.tls | type == "object")' "$bundle" >/dev/null || die 'Invalid configuration bundle schema.'
if [[ $role != agent ]]; then
    install -d -m 0700 "$release/tls"
    jq -er '.tls.certificatePem' "$bundle" > "$release/tls/server.crt"
    jq -er '.tls.privateKeyPem' "$bundle" > "$release/tls/server.key"
    openssl x509 -in "$release/tls/server.crt" -checkend 86400 -noout >/dev/null
    certificate_key=$(openssl x509 -in "$release/tls/server.crt" -pubkey -noout | openssl pkey -pubin -outform DER | sha256sum)
    private_key=$(openssl pkey -in "$release/tls/server.key" -pubout -outform DER | sha256sum)
    [[ $certificate_key == "$private_key" ]] || die 'TLS certificate/key mismatch.'
else
    jq -e '(.tls | has("privateKeyPem") or has("certificatePem") | not)' "$bundle" >/dev/null ||
        die 'Workers must not receive server TLS keys/certificates.'
fi
cat /etc/ssl/certs/ca-certificates.crt > "$release/ca.crt"
jq -r '.tls.caPem // empty' "$bundle" >> "$release/ca.crt"
if [[ $role != agent ]]; then
    certificate_hostname="$(hostname -s).$dns"
    if [[ $role == frontend ]]; then certificate_hostname=$(jq -er '.frontend.publicHostname' "$bundle"); fi
    openssl verify -CAfile "$release/ca.crt" -purpose sslserver -verify_hostname "$certificate_hostname" \
        "$release/tls/server.crt" >/dev/null || die 'TLS certificate must be trusted and cover this role hostname.'
fi

state="/var/lib/kardia/$role"
uid=1654
args=(--name "$name" --network host --restart unless-stopped --stop-timeout 45
    --read-only --tmpfs "/tmp:rw,nosuid,size=1g" --cap-drop ALL --security-opt no-new-privileges:true
    --log-driver local --log-opt max-size=10m --log-opt max-file=5
    --mount "type=bind,source=$release/ca.crt,target=/certs/ca.crt,readonly")
if [[ $role != agent ]]; then args+=(--mount "type=bind,source=$release/tls,target=/tls,readonly"); fi
if [[ $role == frontend ]]; then
    uid=1000
    jq -e '.frontend.publicHostname | test("^[a-zA-Z0-9][a-zA-Z0-9.-]+[a-zA-Z0-9]$")' "$bundle" >/dev/null ||
        die 'Invalid public frontend hostname.'
    jq -e '[.frontend.apiOrigins, .frontend.controllerOrigins] | all(.[];
        type == "array" and length >= 2 and all(.[]; test("^https://[a-zA-Z0-9.-]+:[0-9]+$")))' "$bundle" >/dev/null ||
        die 'Frontend requires at least two HTTPS origins for each backend role.'
    {
        printf 'KARDIA_PUBLIC_HOSTNAME=%s\n' "$(jq -er '.frontend.publicHostname' "$bundle")"
        printf 'KARDIA_API_ORIGINS=%s\n' "$(jq -er '.frontend.apiOrigins | join(" ")' "$bundle")"
        printf 'KARDIA_CONTROLLER_ORIGINS=%s\n' "$(jq -er '.frontend.controllerOrigins | join(" ")' "$bundle")"
    } > "$release/frontend.env"
    args+=(--env-file "$release/frontend.env")
else
    jq -e '.settings | type == "object"' "$bundle" >/dev/null || die 'Missing application settings.'
    if [[ $role == agent ]]; then
        jq -e '.settings.Agent.Authentication.Mode == "ManagedIdentity" and
            (.settings.Agent | has("Id") | not) and
            .settings.Agent.AllowInsecureTransport != true and
            (.settings.Agent.Engines | type == "array" and length >= 2 and
                all(.[]; test("^https://[a-zA-Z0-9.-]+:[0-9]+$")))' "$bundle" >/dev/null ||
            die 'Worker settings require managed identity, multiple HTTPS engines and no shared UUID.'
    else
        jq -e '.settings.Authentication.Mode == "Entra" and .settings.Storage.Provider == "AzureBlob"' "$bundle" >/dev/null ||
            die 'Production server settings require Entra and shared Azure blobs.'
        port=8444
        if [[ $role == api ]]; then
            port=8443
            jq -e '.settings.Engine.Enabled == false and .settings.Engine.ExecuteRuns == false' "$bundle" >/dev/null ||
                die 'API settings must disable engine scheduling and execution.'
        else
            jq -e '.settings.Engine.Enabled == true and .settings.Engine.ExecuteRuns == true' "$bundle" >/dev/null ||
                die 'Controller settings must enable engine scheduling and execution.'
            args+=(-e "Engine__PublicEndpoint=https://$(hostname -s).$dns:8444")
        fi
        jq -e --arg url "https://0.0.0.0:$port" '
            .settings.Kestrel.Endpoints.PrivateHttps |
            .Url == $url and .Protocols == "Http1AndHttp2" and
            .Certificate.Path == "/tls/server.crt" and .Certificate.KeyPath == "/tls/server.key"' "$bundle" >/dev/null ||
            die 'Server settings require the private Kestrel TLS endpoint and mounted certificate paths.'
    fi
    jq -c '.settings' "$bundle" > "$release/appsettings.Production.json"
    args+=(-e DOTNET_ENVIRONMENT=Production -e ASPNETCORE_ENVIRONMENT=Production
        -e "SSL_CERT_FILE=/certs/ca.crt" -e "Runtime__NodeName=$(hostname -s)"
        --mount "type=bind,source=$release/appsettings.Production.json,target=/app/appsettings.Production.json,readonly")
fi
install -d -m 0700 -o "$uid" -g "$uid" "$state"
if [[ $role == agent ]]; then
    if [[ ! -f $state/identity ]]; then
        [[ -z $(find "$state" -mindepth 1 -maxdepth 1 -print -quit) ]] ||
            { echo 'Missing agent UUID alongside existing state; restore identity instead of registering another worker.' >&2; exit 1; }
        cat /proc/sys/kernel/random/uuid > "$state/identity"
        chown "$uid:$uid" "$state/identity"
    fi
    identity=$(cat "$state/identity")
    [[ $identity =~ ^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$ &&
        $identity != 00000000-0000-0000-0000-000000000000 ]] || die 'Invalid persisted agent UUID; restore its original identity.'
    args+=(-e "Agent__Id=$identity" -e "Agent__Name=$(hostname -s)"
        -e Agent__ResultJournalDirectory=/state/results -e Agent__LogSpoolDirectory=/state/logs
        -e Agent__PackageCache=/state/packages --mount "type=bind,source=$state,target=/state")
elif [[ $role != frontend ]]; then
    args+=(--mount "type=bind,source=$state,target=/home/app/.aspnet")
fi
if [[ $role != agent ]]; then
    chown -R "$uid:$uid" "$release/tls"
    chmod 0500 "$release/tls"
    chmod 0400 "$release/tls/server.crt" "$release/tls/server.key"
fi
chmod 0444 "$release/ca.crt"
if [[ $role != frontend ]]; then
    chown "$uid:$uid" "$release/appsettings.Production.json"
    chmod 0400 "$release/appsettings.Production.json"
fi
az acr login --name "$registry" --expose-token --query accessToken --output tsv |
    docker login "$login_server" --username 00000000-0000-0000-0000-000000000000 --password-stdin >/dev/null
docker pull "$image" >/dev/null
docker run --detach "${args[@]}" "$image" >/dev/null
echo "Started $name from immutable image. Verify its application inventory/readiness before advancing."
