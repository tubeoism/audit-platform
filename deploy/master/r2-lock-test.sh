#!/usr/bin/env bash
# Verifies the R2 bucket lock on audit-archive/archive/: writes a small marker object, then tries to delete it.
# Expected: the PUT succeeds and the DELETE is refused with HTTP 409 (object locked).
# Run on the VM as root:  sudo bash r2-lock-test.sh
# Reads R2_ACCOUNT_ID / R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY from /etc/audit-master/master.env.
# Note: the marker cannot be removed until its retention expires; it is a few bytes.
set -euo pipefail

BUCKET="${BUCKET:-audit-archive}"
ENV_FILE="${ENV_FILE:-/etc/audit-master/master.env}"
set -a; . "$ENV_FILE"; set +a
: "${R2_ACCOUNT_ID:?missing}" "${R2_ACCESS_KEY_ID:?missing}" "${R2_SECRET_ACCESS_KEY:?missing}"

ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
KEY="archive/_locktest/$(date -u +%Y%m%dT%H%M%SZ)-$(hostname -s).txt"
URL="$ENDPOINT/$BUCKET/$KEY"
SIG=(--aws-sigv4 "aws:amz:auto:s3" --user "${R2_ACCESS_KEY_ID}:${R2_SECRET_ACCESS_KEY}")

put=$(printf 'lock test %s\n' "$KEY" | curl -s -o /dev/null -w '%{http_code}' "${SIG[@]}" -X PUT --data-binary @- "$URL")
echo "PUT    $BUCKET/$KEY -> HTTP $put"
[[ "$put" == 200 ]] || { echo "FAIL: could not write marker (check token scope)"; exit 1; }

del=$(curl -s -o /tmp/r2-del.xml -w '%{http_code}' "${SIG[@]}" -X DELETE "$URL")
echo "DELETE $BUCKET/$KEY -> HTTP $del"
if [[ "$del" == 409 ]]; then
  echo "PASS: object lock refused the delete"
else
  echo "FAIL: expected 409, got $del"; cat /tmp/r2-del.xml; echo; exit 1
fi
