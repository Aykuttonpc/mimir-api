#!/usr/bin/env bash
# ADR-017: FCM service account JSON'ı VPS'e upload eder.
# Tek seferlik (key rotate edildiğinde tekrar çalıştır).
#
# Usage:
#   bash scripts/deploy-fcm-key.sh
#
# Önkoşul:
#   - SSH key: ~/.ssh/hetzner_aopc
#   - Local: D:/Projeler/mimir-api/secrets/fcm-service-account.json mevcut
#   - VPS: /opt/mimir/secrets/ klasörü (yoksa script oluşturur)

set -euo pipefail

VPS_USER=deploy
VPS_HOST=178.104.198.249
SSH_KEY=~/.ssh/hetzner_aopc

LOCAL_KEY="${MIMIR_API_REPO:-D:/Projeler/mimir-api}/secrets/fcm-service-account.json"
REMOTE_DIR="/opt/mimir/secrets"
REMOTE_KEY="$REMOTE_DIR/fcm-service-account.json"

[[ -f "$LOCAL_KEY" ]] || { echo "❌ FCM key yok: $LOCAL_KEY"; exit 1; }

echo "🔐 FCM service account upload → $VPS_HOST:$REMOTE_KEY"

# Klasörü oluştur + sahibi ayarla (compose mount'u root'a bind etmesin)
ssh -i "$SSH_KEY" "$VPS_USER@$VPS_HOST" \
    "sudo mkdir -p $REMOTE_DIR && sudo chown deploy:deploy $REMOTE_DIR && sudo chmod 750 $REMOTE_DIR"

scp -i "$SSH_KEY" "$LOCAL_KEY" "$VPS_USER@$VPS_HOST:$REMOTE_KEY"

# Sadece root + deploy okusun, dünya okumasın
ssh -i "$SSH_KEY" "$VPS_USER@$VPS_HOST" "chmod 640 $REMOTE_KEY"

echo "✅ FCM key upload tamam."
echo ""
echo "Sonraki adım: api container'ı restart et"
echo "  ssh -i $SSH_KEY $VPS_USER@$VPS_HOST"
echo "  cd /opt/mimir && docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --force-recreate api"
