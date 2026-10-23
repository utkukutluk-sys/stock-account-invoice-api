# Sunucu dışı yedek (off-site backup)

> docs/DEPLOYMENT.md bu sürümde henüz yoktu; bölüm ayrı dosya olarak yazıldı. DEPLOYMENT.md oluşunca aşağıdaki
> `## Sunucu dışı yedek` bölümü olduğu gibi oraya taşınabilir.

## Sunucu dışı yedek

Yedekler (`backups/*.bak` + `.json` manifest) sunucunun kendi diskinde kalırsa disk/VPS kaybında hepsi gider.
Bu yüzden her başarılı yedeğin bir kopyası S3 uyumlu bir bucket'a (AWS S3, Backblaze B2 S3 API, Cloudflare R2,
Hetzner Object Storage, MinIO ...) yüklenir. Sunucuda Docker dışında bir şey gerekmez: `rclone` sabitlenmiş
`rclone/rclone:1.68.2` imajıyla container içinde çalışır.

### 1. Bucket ve anahtar hazırlığı (kullanıcının yapacağı)
1. Sağlayıcıda **özel (private)** bir bucket oluşturun (örn. `stockaccount-backups`). Sürüm geçmişi (versioning) açık olursa iyi olur.
2. Yalnızca bu bucket'a okuma/yazma/silme/listeleme yetkisi olan bir erişim anahtarı üretin (hesap kök anahtarı değil).
3. Aşağıdaki değerleri sunucudaki `.env` dosyasına ekleyin (`.env` git'e girmez; anahtarlar başka yere yazılmaz, loglara basılmaz):

| Değişken | Anlamı | Örnek |
|---|---|---|
| `OFFSITE_ENABLED` | `true` = yedek sonrası otomatik yükleme açık | `true` |
| `OFFSITE_S3_ENDPOINT` | S3 adresi; **AWS S3 için boş bırakın** | `https://s3.eu-central-003.backblazeb2.com` / `https://<hesap>.r2.cloudflarestorage.com` / `https://fsn1.your-objectstorage.com` |
| `OFFSITE_S3_REGION` | Bölge (R2 için `auto`, AWS için örn. `eu-central-1`) | `eu-central-1` |
| `OFFSITE_S3_BUCKET` | Bucket adı | `stockaccount-backups` |
| `OFFSITE_S3_ACCESS_KEY` / `OFFSITE_S3_SECRET_KEY` | Erişim anahtarı çifti | |
| `OFFSITE_PREFIX` | Bucket içindeki klasör (varsayılan `stockaccount`) | `prod` |
| `OFFSITE_RETENTION_DAYS` | Bucket'ta bu günden eski yedekler silinir (varsayılan 30, `0` = hiç silme) | `30` |
| `OFFSITE_MIN_KEEP` | Ne kadar eski olursa olsun en yeni N yedek asla silinmez (varsayılan 3) | `3` |

### 2. Nasıl çalışır
- `scripts/db/offsite-sync.ps1`: yalnızca manifesti (`.json`) olan, yani tamamlanmış yedekleri yükler (önce `.bak`, en son manifest),
  her dosyayı **boyut + checksum** ile doğrular (`rclone check`), sonra bucket'taki eski yedekleri `OFFSITE_RETENTION_DAYS` /
  `OFFSITE_MIN_KEEP` kurallarına göre temizler. Herhangi bir adım başarısızsa çıkış kodu sıfırdan farklıdır (1 = yükleme/doğrulama, 2 = ayar eksik).
- `backup.ps1`: `OFFSITE_ENABLED=true` ise her yedekten sonra yüklemeyi kendisi çalıştırır. Bu örtük modda bucket arızası
  deploy/restore akışlarını **bloklamaz** (uyarı verir, yerel yedek sağlam kalır). `-Offsite` verilirse (zamanlanmış görev) yükleme
  hatası çıkış kodu 5 ile görünür; `-SkipOffsite` yüklemeyi atlar.
- Compose sidecar: `docker compose -f docker-compose.yml -f scripts/db/docker-compose.backup.yml up -d` komutundaki `offsite-sync`
  servisi `./backups` klasörünü 60 sn'de bir izler; yeni yedek çıkınca yükler, en az 6 saatte bir yeniden doğrular ve budar.
  `OFFSITE_ENABLED=true` değilse boşta bekler.
- Zamanlama (Windows): `register-backup-task.ps1 -Target docker -Offsite`. Linux: aşağıya bakın.

### 3. Linux sunucuda zamanlama (PowerShell 7)
```bash
# systemd timer (önerilen) - root gerekir; -Install vermezseniz unit dosyaları yalnızca ekrana basılır
sudo pwsh ./scripts/db/register-backup-task.ps1 -Target docker -At 02:30 -RetentionCount 14 -Offsite -Install
systemctl list-timers stockaccount-db-backup-docker.timer
journalctl -u stockaccount-db-backup-docker.service
# veya cron (kullanıcının crontab'ına yazar)
pwsh ./scripts/db/register-backup-task.ps1 -Target docker -At 02:30 -Offsite -Scheduler cron -Install
# kaldırma: aynı komut + -Unregister
```
Alternatif: compose sidecar'ı (`mssql-backup` + `offsite-sync`) kullanmak; o zaman systemd/cron gerekmez.
Elle deneme: `pwsh ./scripts/db/offsite-sync.ps1` ve bucket içeriği için `pwsh ./scripts/db/offsite-restore.ps1 -List`.

### 4. Felaket kurtarma (yeni sunucu -> kur -> indir -> geri yükle -> deploy)
1. **Yeni sunucu**: Linux VPS'e Docker (Compose v2) ve PowerShell 7 kurun; depoyu klonlayın (`git clone ... StockAccountApp`).
2. **`.env`**: eski sunucunun `.env` kopyasını (parola yöneticisi/güvenli kasadan) yerleştirin. Özellikle `MSSQL_SA_PASSWORD`,
   JWT/diğer sırlar ve `OFFSITE_S3_*` değerleri aynı olmalı. `.env` kayıpsa `scripts/deploy/new-secrets.ps1` ile yeni sırlar üretilir
   (o durumda kullanıcı oturumları/şifreli veriler yeniden oluşur), `OFFSITE_S3_*` değerlerini bucket sağlayıcısından yeniden girin.
3. **Boş veritabanı sunucusunu başlatın**: `docker compose up -d mssql redis` ve `docker compose ps` ile `healthy` olmasını bekleyin.
   (API'yi henüz başlatmayın.)
4. **Yedeği indirin**:
   ```bash
   pwsh ./scripts/db/offsite-restore.ps1 -List                      # bucket'taki yedekler
   pwsh ./scripts/db/offsite-restore.ps1 -Latest -Label scheduled   # en yeni zamanlanmış yedek (veya -File <ad>.bak)
   ```
   Dosyalar `./backups` içine iner; SHA-256 manifestle karşılaştırılır, uyuşmazlıkta hiçbir şey bırakılmaz (çıkış kodu 1).
5. **Geri yükleyin**: `pwsh ./scripts/db/restore.ps1 -Target docker -File <indirilen>.bak -Force`
   (CHECKDB, migration head ve tablo satır sayıları manifestle doğrulanır; çıkış 0 olmalı).
6. **Deploy**: normal dağıtım akışını çalıştırın (`scripts/deploy/deploy.ps1`, bkz. deploy belgeleri). Migration head
   yedektekiyle aynıysa şema değişmez, yeni sürümse migration uygulanır. `curl http://127.0.0.1:5080/health` ve bir giriş denemesiyle kontrol edin.
7. **Zamanlamayı yeniden kurun** (bölüm 3) ve `pwsh ./scripts/db/offsite-sync.ps1` ile yüklemenin çalıştığını doğrulayın.

Notlar: yedek yalnızca aynı veya daha yeni SQL Server ana sürümüne geri yüklenir (prod imajı 2022). Kurtarma noktası, bucket'taki
son yedeğin saatidir; yedekleme sıklığı kadar veri kaybı olabilir. Yılda en az bir kez 4.-5. adımı geçici bir sunucuda deneyin.
