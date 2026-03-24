#!/usr/bin/env bash
# test-samples.sh — Run and validate all Bella Baxter .NET SDK samples.
#
# Usage:  ./test-samples.sh <api-key>
#         ./test-samples.sh bax-myKeyId-mySecret
#
# Samples tested:
#   01-dotenv-file    — bella secrets get -o .env → dotnet run
#   02-process-inject — bella run -- dotnet run
#   03-aspnet         — bella exec -- dotnet run (ASP.NET server, curl validation)
#
# Sample 04-aspire is skipped (requires manual Aspire infrastructure review).

set -uo pipefail

# ─── Paths ──────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLES_DIR="$SCRIPT_DIR/samples"
DEMO_ENV_FILE="$SCRIPT_DIR/../../../demo.env"

# ─── Arguments ──────────────────────────────────────────────────────────────
BELLA_API_KEY="${1:-}"
if [[ -z "$BELLA_API_KEY" ]]; then
  echo "Usage: $0 <api-key>   e.g. $0 bax-myKeyId-mySecret"
  exit 1
fi
if [[ ! -f "$DEMO_ENV_FILE" ]]; then
  echo "demo.env not found: $DEMO_ENV_FILE"
  exit 1
fi

# ─── Config ─────────────────────────────────────────────────────────────────
export BELLA_BAXTER_URL="http://localhost:5522"
SERVER_PORT=5099
SERVER_STARTUP_TIMEOUT=30

# ─── Expected values from demo.env ──────────────────────────────────────────
get_env() { grep -m1 "^${1}=" "$DEMO_ENV_FILE" | cut -d'=' -f2-; }

# APP_CONFIG is stored with outer double-quotes in dotenv; strip them + unescape
raw_app_config="$(get_env APP_CONFIG)"
raw_app_config="${raw_app_config#\"}"
raw_app_config="${raw_app_config%\"}"
EXP_APP_CONFIG="${raw_app_config//\\\"/\"}"   # unescape \" → "

EXP_PORT="$(get_env PORT)"
EXP_DB_URL="$(get_env DATABASE_URL)"
EXP_API_KEY="$(get_env EXTERNAL_API_KEY)"
EXP_GLEAP_KEY="$(get_env GLEAP_API_KEY)"
EXP_ENABLE_FEATURES="$(get_env ENABLE_FEATURES)"
EXP_APP_ID="$(get_env APP_ID)"
EXP_CONN_STRING="$(get_env ConnectionStrings__Postgres)"

# ─── Tracking ────────────────────────────────────────────────────────────────
PASS=0
FAIL=0
RESULTS=()

pass() {
  printf "  \xE2\x9C\x85 %s\n" "$1"
  RESULTS=("${RESULTS[@]+"${RESULTS[@]}"}" "PASS: $1")
  PASS=$((PASS + 1))
}
fail() {
  printf "  \xE2\x9D\x8C %s -- %s\n" "$1" "$2"
  RESULTS=("${RESULTS[@]+"${RESULTS[@]}"}" "FAIL: $1 -- $2")
  FAIL=$((FAIL + 1))
}
section() { printf "\n\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80 %s %s\n" "$1" "$(printf '\xe2\x94\x80%.0s' {1..50})"; }

check() {
  local name="$1" output="$2" pattern="$3"
  if printf '%s' "$output" | grep -qF "$pattern"; then
    pass "$name"
  else
    fail "$name" "expected '$pattern'"
  fi
}

check_all_secrets() {
  local prefix="$1" output="$2"
  check "$prefix: PORT"                      "$output" "PORT=$EXP_PORT"
  check "$prefix: DATABASE_URL"              "$output" "DATABASE_URL=$EXP_DB_URL"
  check "$prefix: EXTERNAL_API_KEY"          "$output" "EXTERNAL_API_KEY=$EXP_API_KEY"
  check "$prefix: GLEAP_API_KEY"             "$output" "GLEAP_API_KEY=$EXP_GLEAP_KEY"
  check "$prefix: ENABLE_FEATURES"           "$output" "ENABLE_FEATURES=$EXP_ENABLE_FEATURES"
  check "$prefix: APP_ID"                    "$output" "APP_ID=$EXP_APP_ID"
  check "$prefix: ConnectionStrings__Postgres" "$output" "ConnectionStrings__Postgres=$EXP_CONN_STRING"
  check "$prefix: APP_CONFIG"                "$output" "APP_CONFIG=$EXP_APP_CONFIG"
}

# ─── Server helpers ──────────────────────────────────────────────────────────
cleanup_port() {
  local port="${1:-$SERVER_PORT}"
  local pids
  pids="$(lsof -ti :"$port" 2>/dev/null)" || true
  if [[ -n "$pids" ]]; then
    while IFS= read -r pid; do
      kill "$pid" 2>/dev/null || true
    done <<< "$pids"
    sleep 1
  fi
}

wait_for_server() {
  local url="$1" timeout="$2"
  local i=0
  while [[ $i -lt $timeout ]]; do
    if curl -sf "$url" > /dev/null 2>&1; then
      return 0
    fi
    sleep 1
    i=$((i + 1))
  done
  return 1
}

# ─── Auth ────────────────────────────────────────────────────────────────────
section "Authentication"
bella login --api-key "$BELLA_API_KEY" > /dev/null 2>&1
if [[ $? -eq 0 ]]; then
  pass "bella login --api-key"
else
  fail "bella login --api-key" "login failed — cannot continue"
  exit 1
fi

# ─── Sample 01: dotenv-file ───────────────────────────────────────────────────
section "01-dotenv-file"
SAMPLE_01="$SAMPLES_DIR/01-dotenv-file"
pushd "$SAMPLE_01" > /dev/null
  # Write .env file using bella secrets get
  if bella secrets get --app dotnet-01-dotenv-file -o .env > /dev/null 2>&1; then
    pass "bella secrets get -o .env"
  else
    fail "bella secrets get -o .env" "command failed"
  fi

  OUTPUT="$(dotnet run 2>&1)"
  check_all_secrets "01" "$OUTPUT"

  rm -f .env
popd > /dev/null

# ─── Sample 02: process-inject ───────────────────────────────────────────────
section "02-process-inject"
SAMPLE_02="$SAMPLES_DIR/02-process-inject"
pushd "$SAMPLE_02" > /dev/null
  OUTPUT="$(bella run --app dotnet-02-process-inject -- dotnet run 2>&1)"
  check_all_secrets "02" "$OUTPUT"
popd > /dev/null

# ─── Sample 03: ASP.NET server ───────────────────────────────────────────────
section "03-aspnet"
SAMPLE_03="$SAMPLES_DIR/03-aspnet"
cleanup_port "$SERVER_PORT"

SERVER_PID=""
pushd "$SAMPLE_03" > /dev/null
  ASPNETCORE_URLS="http://localhost:$SERVER_PORT" \
    bella exec --app dotnet-03-aspnet -- dotnet run > /tmp/bella-dotnet-03.log 2>&1 &
  SERVER_PID=$!

  if wait_for_server "http://localhost:$SERVER_PORT/health" "$SERVER_STARTUP_TIMEOUT"; then
    pass "server started"
  else
    fail "server started" "did not respond within ${SERVER_STARTUP_TIMEOUT}s"
    cat /tmp/bella-dotnet-03.log
    kill "$SERVER_PID" 2>/dev/null || true
    cleanup_port "$SERVER_PORT"
    popd > /dev/null
    # continue to summary
    printf "\n\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80 Summary %s\n" "$(printf '\xe2\x94\x80%.0s' {1..50})"
    printf "PASS: %d  FAIL: %d  TOTAL: %d\n" "$PASS" "$FAIL" "$((PASS + FAIL))"
    [[ $FAIL -eq 0 ]] && exit 0 || exit 1
  fi

  # GET / returns all 8 secrets as JSON
  ROOT_RESPONSE="$(curl -sf "http://localhost:$SERVER_PORT/" 2>&1)"
  # JSON keys look like: "PORT":"8080"  or  "PORT": "8080"
  # Normalise: treat the JSON body as plain text and grep for key:value patterns
  check "03 / PORT"                       "$ROOT_RESPONSE" "\"$EXP_PORT\""
  check "03 / DATABASE_URL"               "$ROOT_RESPONSE" "$EXP_DB_URL"
  check "03 / EXTERNAL_API_KEY"           "$ROOT_RESPONSE" "$EXP_API_KEY"
  check "03 / GLEAP_API_KEY"              "$ROOT_RESPONSE" "$EXP_GLEAP_KEY"
  check "03 / ENABLE_FEATURES"            "$ROOT_RESPONSE" "\"$EXP_ENABLE_FEATURES\""
  check "03 / APP_ID"                     "$ROOT_RESPONSE" "$EXP_APP_ID"
  check "03 / ConnectionStrings__Postgres" "$ROOT_RESPONSE" "$EXP_CONN_STRING"
  check "03 / APP_CONFIG"                 "$ROOT_RESPONSE" "setting1"

  kill "$SERVER_PID" 2>/dev/null || true
  cleanup_port "$SERVER_PORT"
popd > /dev/null
rm -f /tmp/bella-dotnet-03.log

# ─── Summary ─────────────────────────────────────────────────────────────────
printf "\n\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80 Summary %s\n" "$(printf '\xe2\x94\x80%.0s' {1..50})"
for r in "${RESULTS[@]+"${RESULTS[@]}"}"; do
  echo "  $r"
done
printf "\nPASS: %d  FAIL: %d  TOTAL: %d\n" "$PASS" "$FAIL" "$((PASS + FAIL))"

if [[ $FAIL -gt 0 ]]; then
  exit 1
fi
