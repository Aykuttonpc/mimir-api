#!/usr/bin/env bash
# T-043: Mimir APK build + VPS upload + force-update env update + api restart.
#
# Usage:
#   bash scripts/deploy-apk.sh <new-version> [--force]
# Example:
#   bash scripts/deploy-apk.sh 0.2.0          # uploads APK, sets LATEST=0.2.0
#   bash scripts/deploy-apk.sh 0.2.0 --force  # additionally bumps MIN to 0.2.0 (eski APK 426)
#
# Önkoşul:
#   - SSH key: ~/.ssh/hetzner_aopc
#   - Mobile repo: D:/Projeler/InstaClone (kmp-rewrite branch)
#   - app/build/outputs/apk/debug/app-debug.apk hazır olmalı (gradle build önce)
#
# Sınır: bu script DEBUG APK'yı uploader. Production için release-signed APK kullan.

set -euo pipefail

VERSION="${1:?VERSION arg gerekli (örn 0.2.0)}"
FORCE_BUMP=false
if [[ "${2:-}" == "--force" ]]; then FORCE_BUMP=true; fi

VPS_USER=deploy
VPS_HOST=178.104.198.249
SSH_KEY=~/.ssh/hetzner_aopc

MOBILE_REPO="${MIMIR_MOBILE_REPO:-D:/Projeler/InstaClone}"
APK_LOCAL="$MOBILE_REPO/app/build/outputs/apk/debug/app-debug.apk"
REMOTE_APK="/opt/mimir/apks/mimir-${VERSION}.apk"
DOWNLOAD_URL="https://aykutonpc.com/mimir/app/mimir-${VERSION}.apk"

[[ -f "$APK_LOCAL" ]] || { echo "❌ APK yok: $APK_LOCAL — önce gradle build yap"; exit 1; }

echo "📦 APK boyutu: $(du -h "$APK_LOCAL" | cut -f1)"
echo "📤 Upload → $VPS_HOST:$REMOTE_APK"

ssh -i "$SSH_KEY" "$VPS_USER@$VPS_HOST" "sudo mkdir -p /opt/mimir/apks && sudo chown deploy:deploy /opt/mimir/apks"
scp -i "$SSH_KEY" "$APK_LOCAL" "$VPS_USER@$VPS_HOST:$REMOTE_APK"

echo "⚙️  .env.prod update"
ssh -i "$SSH_KEY" "$VPS_USER@$VPS_HOST" bash -s <<EOF
  set -e
  cd /opt/mimir
  if grep -q "^LATEST_APP_VERSION_ANDROID=" .env.prod; then
    sed -i "s|^LATEST_APP_VERSION_ANDROID=.*|LATEST_APP_VERSION_ANDROID=$VERSION|" .env.prod
  else
    echo "LATEST_APP_VERSION_ANDROID=$VERSION" >> .env.prod
  fi
  if grep -q "^APP_DOWNLOAD_URL_ANDROID=" .env.prod; then
    sed -i "s|^APP_DOWNLOAD_URL_ANDROID=.*|APP_DOWNLOAD_URL_ANDROID=$DOWNLOAD_URL|" .env.prod
  else
    echo "APP_DOWNLOAD_URL_ANDROID=$DOWNLOAD_URL" >> .env.prod
  fi
  if [[ "$FORCE_BUMP" == "true" ]]; then
    if grep -q "^MIN_APP_VERSION_ANDROID=" .env.prod; then
      sed -i "s|^MIN_APP_VERSION_ANDROID=.*|MIN_APP_VERSION_ANDROID=$VERSION|" .env.prod
    else
      echo "MIN_APP_VERSION_ANDROID=$VERSION" >> .env.prod
    fi
    echo "🔒 MIN_APP_VERSION_ANDROID = $VERSION (eski APK'lar 426 alacak)"
  fi
  grep -E "^(LATEST_APP_VERSION_ANDROID|APP_DOWNLOAD_URL_ANDROID|MIN_APP_VERSION_ANDROID)=" .env.prod
EOF

echo "♻️  api container restart"
ssh -i "$SSH_KEY" "$VPS_USER@$VPS_HOST" "cd /opt/mimir && docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --no-deps api"

echo "🧪 Smoke: /api/app/version"
sleep 5
curl -sS "https://aykutonpc.com/mimir/api/app/version" | python3 -m json.tool || true

echo "🔗 APK URL: $DOWNLOAD_URL"
echo "✅ Done"
