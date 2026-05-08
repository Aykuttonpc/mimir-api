# InstaClone — Deployment

VPS deploy artifact'ları. Pattern: VPS rehberi (`D:\AYKUTONPC-VPS-REHBERI.md`) "Strateji A".

## VPS Hedef

- **Sunucu:** Hetzner CPX22, `178.104.198.249`, Ubuntu 24.04
- **Yer:** `/opt/instaclone`
- **SSH:** `ssh -i ~/.ssh/hetzner_aopc deploy@178.104.198.249`
- **Mevcut komşu:** AykutOnPC (`/opt/aykutonpc`) — aynı VPS, ayrı klasör, ayrı container'lar

## Container Topology

| Container | Image | Port | Mem Limit | Not |
|---|---|---|---|---|
| `instaclone-db` | postgres:16-alpine | (yok, internal) | 512m | EF Core hedefi |
| `instaclone-redis` | redis:7-alpine | (yok, internal) | 256m | requirepass + LRU 200m |
| `instaclone-web` | ghcr.io/aykuttonpc/instaclone-api:${WEB_TAG} | `127.0.0.1:9001` | 1g | T-007 sonrası aktive |

## Routing

Mevcut AykutOnPC nginx (port 80/443) `/insta/` path prefix ile bizim 9001'e proxy eder. Bkz `nginx/instaclone.conf` ve ADR-007.

## İlk Deploy Adımları (T-006 + T-008)

```bash
# 1) VPS'e bağlan
ssh -i ~/.ssh/hetzner_aopc deploy@178.104.198.249

# 2) Klasörü hazırla
sudo mkdir -p /opt/instaclone && sudo chown deploy:deploy /opt/instaclone
cd /opt/instaclone

# 3) Local'den dosyaları çek (kullanıcı makinesinden)
#    scp deployment/docker-compose.prod.yml deploy@178.104.198.249:/opt/instaclone/
#    scp deployment/.env.prod.example       deploy@178.104.198.249:/opt/instaclone/

# 4) .env.prod oluştur (random secret'larla doldur)
cp .env.prod.example .env.prod
# openssl rand -base64 32  → DB_PASSWORD, REDIS_PASSWORD
# openssl rand -base64 64  → JWT_KEY
nano .env.prod

# 5) Sadece DB + Redis ayağa kaldır (web henüz yorumlanmış)
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d db redis

# 6) Healthcheck doğrula
docker compose -f docker-compose.prod.yml ps
docker exec instaclone-db pg_isready -U instaclone
docker exec instaclone-redis redis-cli -a "$REDIS_PASSWORD" ping
```

## ⚠️ ASLA YAPMA

- `docker compose down -v` → DB volume silinir, **TÜM VERİ GİDER** (VPS rehberi 257)
- `image: ...:latest` → versiyon belirsizliği, rollback imkânsız (semver veya SHA)
- Port `0.0.0.0:9001` → dış expose; her zaman `127.0.0.1:9001`, nginx üzerinden çık
- `mem_limit` olmadan container → bir OOM tüm host'u boğar (komşu AykutOnPC dahil)
- AykutOnPC nginx config'ini `git reset --hard` ile sıfırla (rehber 218 — geçici cert fix silinir)

## Bağlantılı Dökümanlar

- VPS rehberi: `D:\AYKUTONPC-VPS-REHBERI.md`
- Mimari: `.claudeteam/ARCHITECTURE.md`
- Kararlar: `.claudeteam/DECISIONS.md` (ADR-006, 007, 008)
- Sprint: `.claudeteam/SPRINT_BOARD.md` (T-006, T-008, T-015)
