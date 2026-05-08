# mimir-api

Backend API for Mimir — kapalı, davet+admin onaylı kişisel iletişim ağı.

## Stack
- ASP.NET Core 9 / C#
- PostgreSQL 16
- Redis 7
- SignalR (real-time DM)
- MinIO (S3-compat medya)
- JWT auth (access + refresh rotation)
- Docker Compose deploy

## Hosting
Hetzner CPX22 VPS (`/opt/mimir`), mevcut nginx üzerinden `/mimir/` path prefix ile servis.

## Repo
- Backend: bu repo
- Mobile: [JavaInstagramClone](https://github.com/Aykuttonpc/JavaInstagramClone) (Sprint #3'te `mimir-mobile`'a rename edilecek)

## Status
**Sprint #2 — iskelet aşaması.** Henüz çalışan kod yok.
