# Canlıya Alma (Tek Linux VPS) - Adım Adım Rehber

Bu rehber StockAccountApp'i **tek bir Linux sunucuya (VPS)** kurmak içindir. Hedef kitle: kurulumu ilk kez yapacak
kişi. Komutların hepsi kopyalanıp yapıştırılabilir; `<...>` içindeki yerleri kendi değerinizle değiştirin.

## 0. Büyük resim

```
Tarayıcı --HTTPS--> Caddy (80/443, otomatik Let's Encrypt sertifikası)
                      |
                      v
                    ui-vue (nginx): Vue arayüzü + /api isteklerini API'ye iletir
                      |
                      v
                     api (ASP.NET Core, sadece iç ağda) --> mssql (SQL Server) + redis
```

- Dışarıya **sadece** 80 ve 443 açıktır (Caddy). API, SQL Server, Redis ve arayüz konteyneri sunucunun hiçbir portuna
  bağlanmaz.
- Sadece **Vue arayüzü** yayındadır. `ui-nuxt` depoda durur ama canlıda çalıştırılmaz.
- Uptime Kuma (izleme) sadece sunucunun kendi içinden (`127.0.0.1:3001`) erişilebilir; SSH tüneliyle bağlanırsınız.
- Her şey Docker Compose ile yönetilir: `docker-compose.yml` (temel) + `docker-compose.prod.yml` (canlı ayarları).
  Deploy script'i ikisini birlikte kullanır; siz `-Environment prod` parametresini vermeniz yeter (`-Production` aynı işi yapan eski takma addır).

## Ortamlar

Aynı depo dört ortamda çalışır. Her ortamın **kendi compose projesi, ağı, volume'ları, env dosyası, sürüm geçmişi ve yedek klasörü** vardır;
birinde yapılan işlem (deploy, geri alma, yedek, yedek silme) ötekilere dokunmaz.

| Ortam | Ne için | Nasıl çalışır | Compose projesi | Env dosyası | Yedekler | Adres |
|---|---|---|---|---|---|---|
| **dev** | Günlük geliştirme, demo veri (`admin@stockaccount.local`) | `docker compose up -d` veya `deploy.ps1 -Version x.y.z` | `stockaccount` | `.env` | `backups/` | `http://localhost:5173` (Vue), `:3000` (Nuxt), `:5080` (API) |
| **test** | Otomatik doğrulama: birim + entegrasyon testleri, Playwright E2E, Postman | `dotnet test`, `e2e`, `postman`; **CI**'da (GitHub Actions `ci.yml`) her push/PR'da; tek kullanımlık veritabanları | kalıcı ortam yok | CI sırları | yok | yok |
| **staging** | Prod'a çıkmadan önce prod'un yakın benzeri: Production modu, Caddy + HTTPS, rate limit, demo veri **yok**, bootstrap yönetici | `deploy.ps1 -Environment staging -Version x.y.z` | `stockaccount-staging` | `.env.staging` | `backups/staging/` | `https://localhost:8443` (Uptime Kuma: `http://127.0.0.1:3002`) |
| **prod** | Gerçek kullanıcılar, tek Linux VPS | `deploy.ps1 -Environment prod -Version x.y.z` | `stockaccount-prod` | `.env` (sunucuda) | `backups/prod/` (+ sunucu dışı) | `https://<alan-adı>` |

Staging, dev yığınıyla **aynı makinede yan yana** çalışır ve onunla hiçbir kaynak paylaşmaz: ayrı ağ (`172.30.40.0/24`; dev `172.30.0.0/24`),
yalnızca Caddy'nin 8080/8443 portları (ve `127.0.0.1:3002`'de Uptime Kuma) yayınlanır; API, SQL Server ve Redis'in host portu yoktur.
Caddy yerel sertifika otoritesi kullanır (`LOCAL_TLS=internal`): tarayıcı bir kez uyarı verir, `curl` için `-k` gerekir.
Dosyalar: `docker-compose.staging.yml` (prod ayarlarının üstüne eklenir), `.env.staging` (git'e girmez).

```powershell
# bir kez: .env.staging üretir (DOMAIN=localhost, LOCAL_TLS=internal, bootstrap yönetici staging-admin@stockaccount.local)
.\scripts\deploy\new-secrets.ps1 -Environment staging
# dağıt (ilk seferde derler); giriş parolası .env.staging içindeki BOOTSTRAP_ADMIN_PASSWORD
.\scripts\deploy\deploy.ps1 -Environment staging -Version 1.6.0
.\scripts\deploy\rollback.ps1 -Environment staging -RestoreDatabase      # önceki sürüme + deploy öncesi yedekten veritabanı
# durdur / başlat (veriler volume'larda kalır)
docker compose -p stockaccount-staging --env-file .env.staging -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.staging.yml stop
docker compose -p stockaccount-staging --env-file .env.staging -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.staging.yml start
# prod-hazırlık denemeleri: varsayılan hedef staging; dev yığınını yalnızca --i-know-this-is-dev ile kabul eder
bash tests/prod-readiness/proxy-ip-check.sh
node tests/prod-readiness/fuzz-errors.mjs
```

**Sürüm akışı:** `dev` (geliştir; birim/entegrasyon/E2E ve CI yeşil) -> `staging` (`deploy.ps1 -Environment staging -Version x.y.z`, ardından **smoke**:
giriş yap, `https://localhost:8443/api/health/ready` = 200, `proxy-ip-check.sh` ve `fuzz-errors.mjs` temiz, gerekirse
`rollback.ps1 -Environment staging -RestoreDatabase` denemesi) -> `prod` (aynı `x.y.z` ile `deploy.ps1 -Environment prod`). Staging'de denenmemiş sürüm prod'a gitmez.

Güvenceler: staging/prod deploy'u, dev yığınının çalıştırdığı imaj etiketini (örn. `stockaccount-api:1.5.8`) yeniden derlemeyi reddeder.
Yedek, geri yükleme ve saklama (retention) yalnızca çağrılan ortamın klasöründe çalışır; başka ortamın klasörü verilirse hata verir
(`tests/prod-readiness/backup-isolation.test.ps1`). Dev yedekleri (`backups/`) bu yüzden staging veya prod saklama kuralıyla silinemez.

## 1. Neye ihtiyacınız var

| Ne | Not |
|---|---|
| VPS | Ubuntu 24.04 LTS. **En az 2 vCPU ve 4 GB RAM; 8 GB RAM önerilir.** 40 GB+ SSD. (4 GB'ta ilk derleme sırasında bellek dolabilir: 2 GB swap ekleyin, bkz. 2.5) |
| Alan adı | Örn. `stok.firmaniz.com`. DNS yönetim paneline erişiminiz olmalı. |
| E-posta | Let's Encrypt sertifika uyarıları için (`ACME_EMAIL`) ve ilk yönetici hesabı için. |
| SSH anahtarı | Sunucuya parolasız, anahtarla bağlanacaksınız. |

SQL Server'ın **Express** sürümü kullanılır (ücretsiz, veritabanı başına 10 GB sınırı). Developer sürümü canlıda lisanssızdır,
bu yüzden `docker-compose.prod.yml` varsayılan olarak Express seçer.

## 2. Sunucu hazırlığı (bir kez)

### 2.1 DNS A kaydı (ilk iş)

DNS panelinde bir **A kaydı** oluşturun: `stok.firmaniz.com` -> sunucunun IPv4 adresi (varsa AAAA -> IPv6).
Kaydın yayılmasını bekleyin ve kontrol edin:

```bash
dig +short stok.firmaniz.com        # sunucunun IP'sini göstermeli
```

> Önemli: DNS doğru olmadan ilk deploy'u yapmayın. Let's Encrypt doğrulaması başarısız olursa kısa sürede deneme sınırına
> takılırsınız (saatte 5 başarısız deneme).

### 2.2 Sisteme ilk giriş, güncelleme, deploy kullanıcısı

Sağlayıcının verdiği bilgiyle root olarak bağlanın: `ssh root@<SUNUCU_IP>`

```bash
apt update && apt -y upgrade
apt -y install unattended-upgrades ca-certificates curl git ufw
dpkg-reconfigure -plow unattended-upgrades      # güvenlik güncellemeleri otomatik gelsin (Evet)

# root olmayan deploy kullanıcısı (kendi SSH anahtarınızı kopyalar)
adduser --disabled-password --gecos "" deploy
usermod -aG sudo deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
cp ~/.ssh/authorized_keys /home/deploy/.ssh/authorized_keys
chown deploy:deploy /home/deploy/.ssh/authorized_keys && chmod 600 /home/deploy/.ssh/authorized_keys
echo 'deploy ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/deploy && chmod 440 /etc/sudoers.d/deploy
```

**Yeni bir terminal açıp** `ssh deploy@<SUNUCU_IP>` ile girebildiğinizi doğrulayın. Çalıştığını görmeden root oturumunu
kapatmayın. Sonra parola ile SSH girişini kapatın (kendinizi kilitlememek için önce deploy ile girişi test ettiniz):

```bash
sudo sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/; s/^#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config
sudo systemctl restart ssh
```

### 2.3 Güvenlik duvarı (ufw): sadece 22, 80, 443

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443          # TCP + UDP (HTTP/3)
sudo ufw --force enable
sudo ufw status verbose
```

(Docker kendi iptables kurallarını yazar; bu rehberde zaten sadece 80/443 yayınlanır, Uptime Kuma `127.0.0.1`'e bağlıdır.)

### 2.4 Docker Engine + Compose eklentisi

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker deploy
```

Grup değişikliğinin geçerli olması için çıkıp tekrar girin (`exit`, sonra `ssh deploy@<SUNUCU_IP>`), sonra:

```bash
docker run --rm hello-world
docker compose version
```

### 2.5 PowerShell 7 (deploy script'leri `pwsh` ile çalışır)

```bash
sudo apt-get install -y wget apt-transport-https software-properties-common
source /etc/os-release
wget -q https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y powershell
pwsh -NoProfile -c '$PSVersionTable.PSVersion'
```

4 GB RAM'li sunucuda (ilk derleme için) swap eklemeniz önerilir:

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

## 3. Uygulamayı indirin ve ayarlayın

```bash
cd ~
git clone https://github.com/utkukutluk-sys/stock-account-invoice-api.git StockAccountApp
cd StockAccountApp

# SQL Server yedek klasörü: SQL Server konteyneri (kullanıcı 10001) buraya yazar
mkdir -p backups/prod && chmod 777 backups backups/prod
```

### 3.1 Gizli bilgileri üretin (`.env`)

```bash
pwsh ./scripts/deploy/new-secrets.ps1 -AdminEmail yonetici@firmaniz.com -Domain stok.firmaniz.com -AcmeEmail yonetici@firmaniz.com
chmod 600 .env
```

Bu komut `.env` dosyasını oluşturur ve rastgele, güçlü değerler yazar (ekrana basmaz):
`JWT_KEY`, `MSSQL_SA_PASSWORD`, `APP_DB_PASSWORD`, `BOOTSTRAP_ADMIN_PASSWORD`, ayrıca `DOMAIN`, `ACME_EMAIL`,
`BOOTSTRAP_ADMIN_EMAIL`.

İsterseniz ilk şirketi de otomatik açtırabilirsiniz (isteğe bağlı): `nano .env` ile `BOOTSTRAP_COMPANY_NAME=` ve
`BOOTSTRAP_COMPANY_TAX_NUMBER=` satırlarını doldurun (vergi no 10 veya 11 haneli). Boş bırakırsanız şirketi sonradan
arayüzden ekleyebilirsiniz. Kaydetmek için `Ctrl+O`, `Enter`; çıkmak için `Ctrl+X`.

> `.env` dosyasını **asla** git'e eklemeyin ve kimseyle paylaşmayın. Yedeğini parola yöneticinizde saklayın.

## 4. İlk deploy

```bash
pwsh ./scripts/deploy/deploy.ps1 -Environment prod -Version 1.0.0
```

Ne yapar: imajları derler (ilk seferde 5-10 dakika), veritabanını oluşturur/geçirir (db-migrate), **ilk yöneticiyi
oluşturur**, tüm servisleri başlatır, API'nin hazır olduğunu (`/health/ready` = 200) doğrular. Hata olursa otomatik
olarak önceki sürüme döner (ilk deploy'da önceki sürüm yoktur; hata mesajına bakın, bkz. Sorun giderme).

Başarıda son satır şuna benzer: `[deploy] DEPLOYED 1.0.0 in 23 s ...`. Durum kontrolü:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
curl -s https://stok.firmaniz.com/api/health/ready      # {"status":"Healthy",...}
```

Her seferinde iki dosya adı yazmamak için kısayol tanımlayın:

```bash
echo "alias dcp='docker compose -f docker-compose.yml -f docker-compose.prod.yml'" >> ~/.bashrc && source ~/.bashrc
dcp ps
```

## 5. Giriş yapma

1. Tarayıcıda `https://stok.firmaniz.com` adresini açın (kilit simgesi görünmeli).
2. Kullanıcı adı: `-AdminEmail` ile verdiğiniz e-posta.
3. Parola: sunucuda şu komutla okuyun:

   ```bash
   grep '^BOOTSTRAP_ADMIN_PASSWORD=' ~/StockAccountApp/.env
   ```

4. Girdikten sonra **parolanızı hemen değiştirin** (arayüzden "Parola değiştir"), yeni parolayı parola yöneticinize kaydedin.
5. Parola değiştikten sonra `.env` içindeki `BOOTSTRAP_ADMIN_PASSWORD=...` satırının değerini silin (`nano .env`).
   Yönetici zaten var olduğundan bu değer bir daha kullanılmaz; silmek, eski parolanın dosyada kalmasını önler.

Notlar:
- Canlıda demo kullanıcılar (`admin@stockaccount.local`, `demo@...`) **yoktur**; demo veri yüklenmez.
- İlk yönetici yalnızca **hiç Admin yokken** oluşturulur. Sonraki deploy'larda dokunulmaz (parola sıfırlanmaz).
- Hiç Admin yokken ve `BOOTSTRAP_ADMIN_EMAIL`/`BOOTSTRAP_ADMIN_PASSWORD` boşken deploy, açık bir hata mesajıyla durur.
- Giriş denemeleri IP başına dakikada 10 ile sınırlıdır; fazlası `429` döner (kaba kuvvet koruması).

## 6. Güncelleme

```bash
cd ~/StockAccountApp
git pull
pwsh ./scripts/deploy/deploy.ps1 -Environment prod -Version 1.0.1
```

- Sürüm numarası her seferinde yenisi olmalı (`1.0.1`, `1.1.0`, ...). Önceki sürümün imajı rollback için sunucuda kalır.
- Script önce **veritabanı yedeği** alır (yedek başarısızsa deploy durur), sonra derler, başlatır, sağlık kontrolü yapar.
  Yeni sürüm hazır olmazsa **kendiliğinden önceki sürüme döner** (veritabanı yedeğinden geri yükleyerek).

## 7. Geri alma (rollback)

Yeni sürümde sorun fark ettiyseniz:

```bash
pwsh ./scripts/deploy/rollback.ps1 -Environment prod                          # bir önceki sürüme (kodu geri alır)
pwsh ./scripts/deploy/rollback.ps1 -Environment prod -RestoreDatabase         # + veritabanını deploy öncesi yedekten geri yükler
pwsh ./scripts/deploy/rollback.ps1 -Environment prod -Version 1.0.0           # belirli bir sürüme
```

`-Environment prod` yazmayı unutmayın (eski `-Production` takma adı da çalışır): geçmiş ve yedekler ortama özeldir, yazmazsanız script geliştirme (dev) ortamını hedefler. Yeni sürüm veritabanı şemasını
değiştirdiyse (yeni migration) eski kod çalışmaz; bu durumda `-RestoreDatabase` kullanın. Dikkat: `-RestoreDatabase`, deploy'dan
sonra girilen verileri geri alır.

## 8. Yedekleme

Her deploy öncesi otomatik yedek alınır. Ayrıca elle ve zamanlanmış yedek:

```bash
# Elle yedek (dosya ~/StockAccountApp/backups/prod altına düşer; prod/staging yedekleri dev dosyalarına asla dokunmaz)
pwsh ./scripts/db/backup.ps1 -Environment prod -Target docker -Label manuel

# Zamanlanmış yedek (varsayılan 24 saatte bir; yedek servisi arka planda çalışır)
docker compose -f docker-compose.yml -f docker-compose.prod.yml -f scripts/db/docker-compose.backup.yml up -d
# (.env içinde BACKUP_DIR=./backups/prod tanımlı olmalı; yedek servisi aynı klasörü kullanır)

# Geri yükleme (önce uygulamayı durdurun: dcp stop api)
pwsh ./scripts/db/restore.ps1 -Environment prod -Target docker -Latest -Label scheduled
```

Ayrıntılar: `docs/DB_NOTES.md` §8. **Yedekler aynı sunucuda durduğu sürece sunucu kaybında yedek de kaybolur**, aşağıdaki bölümü
mutlaka kurun.

### Sunucu dışı yedek

Yedekler (`backups/prod/*.bak` + `.json` manifest) sunucunun kendi diskinde kalırsa disk/VPS kaybında hepsi gider. Bu yüzden her
başarılı yedeğin bir kopyası S3 uyumlu bir bucket'a yüklenir. **Önerilen sağlayıcı: Cloudflare R2** (çıkış/egress ücreti yok, 10 GB
ücretsiz); alternatif: **Backblaze B2** (S3 API). AWS S3, Hetzner Object Storage, MinIO gibi her S3 uyumlu depo da çalışır.
Sunucuda Docker dışında bir şey gerekmez: `rclone`, sabitlenmiş `rclone/rclone:1.68.2` imajıyla container içinde çalışır.

#### 1. Bucket ve anahtar hazırlığı (bir kez, sizin yapacağınız)

**Cloudflare R2 (önerilen)**
1. Cloudflare panelinde *R2 Object Storage* -> *Create bucket*: **özel (private)** bir bucket (örn. `stockaccount-backups`). Herkese açık erişimi (public access / r2.dev) **açmayın**.
2. *R2* -> *Manage R2 API Tokens* -> *Create API token*: yetki **Object Read & Write**, kapsam **yalnızca bu bucket** (hesap geneli token vermeyin).
3. Ekranda bir kez gösterilen **Access Key ID** ve **Secret Access Key** değerlerini parola yöneticinize kaydedin.
4. S3 adresi (endpoint): `https://<ACCOUNT_ID>.r2.cloudflarestorage.com` (`<ACCOUNT_ID>` R2 sayfasında ve token ekranında yazar). Bölge: `auto`.

**Backblaze B2 (alternatif)**
1. *Buckets* -> *Create a Bucket*: **Private**. Dosya sürümlemeyi açık bırakmanız iyi olur.
2. *Application Keys* -> *Add a New Application Key*: **yalnızca bu bucket'a** kısıtlı, *Read and Write* erişimli anahtar. `keyID` = erişim anahtarı, `applicationKey` = gizli anahtar (bir kez gösterilir).
3. Endpoint: bucket ayrıntı sayfasındaki *S3 Endpoint*, örn. `https://s3.eu-central-003.backblazeb2.com`. Bölge: endpoint'teki bölge adı (örn. `eu-central-003`).

Değerleri sunucudaki `.env` dosyasına yazın (`.env` git'e girmez; anahtarlar başka yere yazılmaz, loglara basılmaz). `.env.example` içinde
yorum satırı olarak hazır bir R2 örneği vardır:

| Değişken | Anlamı | Örnek (R2) |
|---|---|---|
| `OFFSITE_ENABLED` | `true` = yedek sonrası otomatik yükleme açık (varsayılan `false`) | `true` |
| `OFFSITE_S3_ENDPOINT` | S3 adresi; **AWS S3 için boş bırakın** | `https://<ACCOUNT_ID>.r2.cloudflarestorage.com` |
| `OFFSITE_S3_REGION` | Bölge (R2 için `auto`, B2 için endpoint'teki bölge, AWS için örn. `eu-central-1`) | `auto` |
| `OFFSITE_S3_BUCKET` | Bucket adı | `stockaccount-backups` |
| `OFFSITE_S3_ACCESS_KEY` / `OFFSITE_S3_SECRET_KEY` | Bucket'a kısıtlı erişim anahtarı çifti | |
| `OFFSITE_PREFIX` | Bucket içindeki klasör (varsayılan `stockaccount`; staging otomatik `stockaccount-staging` kullanır) | `prod` |
| `OFFSITE_RETENTION_DAYS` | Bucket'ta bu günden eski yedekler silinir (varsayılan 30, `0` = hiç silme) | `30` |
| `OFFSITE_MIN_KEEP` | Ne kadar eski olursa olsun en yeni N yedek asla silinmez (varsayılan 3) | `3` |

Doğrulama: `pwsh ./scripts/db/offsite-sync.ps1 -List` (bucket erişimini ve anahtarı sınar), sonra
`pwsh ./scripts/db/backup.ps1 -Environment prod -Label deneme` çalıştırıp çıktıda `Off-site copy` adımının başarıyla bittiğini görün.

#### 2. Nasıl çalışır
- `scripts/db/offsite-sync.ps1`: yalnızca manifesti (`.json`) olan, yani tamamlanmış yedekleri yükler (önce `.bak`, en son manifest),
  her dosyayı **boyut + checksum** ile doğrular (`rclone check`), sonra bucket'taki eski yedekleri `OFFSITE_RETENTION_DAYS` /
  `OFFSITE_MIN_KEEP` kurallarına göre temizler. Herhangi bir adım başarısızsa çıkış kodu sıfırdan farklıdır (1 = yükleme/doğrulama, 2 = ayar eksik).
- `backup.ps1`: `OFFSITE_ENABLED=true` ise her yedekten sonra yüklemeyi kendisi çalıştırır. Bu örtük modda bucket arızası
  deploy/restore akışlarını **bloklamaz** (uyarı verir, yerel yedek sağlam kalır). `-Offsite` verilirse (zamanlanmış görev) yükleme
  hatası çıkış kodu 5 ile görünür; `-SkipOffsite` yüklemeyi atlar.
- Her ortam yalnızca kendi yedek klasörünü yükler (prod: `backups/prod`, staging: `backups/staging`, dev: `backups`) ve kendi bucket klasörünü (`OFFSITE_PREFIX`) kullanır.
- Compose sidecar: `docker compose -f docker-compose.yml -f docker-compose.prod.yml -f scripts/db/docker-compose.backup.yml up -d` komutundaki `offsite-sync`
  servisi `BACKUP_DIR` klasörünü 60 sn'de bir izler; yeni yedek çıkınca yükler, en az 6 saatte bir yeniden doğrular ve budar.
  `OFFSITE_ENABLED=true` değilse boşta bekler.

#### 3. Linux sunucuda zamanlama (PowerShell 7)
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
Zamanlanmış görevin prod klasörünü kullanması için ortamında `STOCKACCOUNT_ENV=prod` tanımlı olmalıdır (systemd unit'ine `Environment=STOCKACCOUNT_ENV=prod`,
cron satırının başına `STOCKACCOUNT_ENV=prod` ekleyin).

#### 4. Felaket kurtarma (yeni sunucu -> kur -> indir -> geri yükle -> deploy)
1. **Yeni sunucu**: Linux VPS'e Docker (Compose v2) ve PowerShell 7 kurun (bu rehberin 2. bölümü); depoyu klonlayın (3. bölüm).
2. **`.env`**: eski sunucunun `.env` kopyasını (parola yöneticisi/güvenli kasadan) yerleştirin. Özellikle `MSSQL_SA_PASSWORD`,
   JWT/diğer sırlar ve `OFFSITE_S3_*` değerleri aynı olmalı. `.env` kayıpsa `scripts/deploy/new-secrets.ps1` ile yeni sırlar üretilir
   (o durumda kullanıcı oturumları/şifreli veriler yeniden oluşur), `OFFSITE_S3_*` değerlerini bucket sağlayıcısından yeniden girin.
3. **Boş veritabanı sunucusunu başlatın**: `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d mssql redis` ve `ps` ile `healthy` olmasını bekleyin.
   (API'yi henüz başlatmayın.)
4. **Yedeği indirin**:
   ```bash
   export STOCKACCOUNT_ENV=prod                                       # prod klasörünü ve compose projesini seçer
   pwsh ./scripts/db/offsite-restore.ps1 -List                        # bucket'taki yedekler
   pwsh ./scripts/db/offsite-restore.ps1 -Latest -Label scheduled     # en yeni zamanlanmış yedek (veya -File <ad>.bak)
   ```
   Dosyalar `./backups/prod` içine iner; SHA-256 manifestle karşılaştırılır, uyuşmazlıkta hiçbir şey bırakılmaz (çıkış kodu 1).
5. **Geri yükleyin**: `pwsh ./scripts/db/restore.ps1 -Environment prod -Target docker -File <indirilen>.bak -Force`
   (CHECKDB, migration head ve tablo satır sayıları manifestle doğrulanır; çıkış 0 olmalı).
6. **Deploy**: `pwsh ./scripts/deploy/deploy.ps1 -Environment prod -Version <x.y.z>`. Migration head yedektekiyle aynıysa şema
   değişmez, yeni sürümse migration uygulanır. `curl https://<alan-adı>/api/health/ready` ve bir giriş denemesiyle kontrol edin.
7. **Zamanlamayı yeniden kurun** (bölüm 3) ve `pwsh ./scripts/db/offsite-sync.ps1` ile yüklemenin çalıştığını doğrulayın.

Notlar: yedek yalnızca aynı veya daha yeni SQL Server ana sürümüne geri yüklenir (prod imajı 2022). Kurtarma noktası, bucket'taki
son yedeğin saatidir; yedekleme sıklığı kadar veri kaybı olabilir. Yılda en az bir kez 4.-5. adımı geçici bir sunucuda deneyin.

## 9. İzleme (Uptime Kuma)

Uptime Kuma, sunucuda `127.0.0.1:3001` üzerinde çalışır (dışarıdan erişilemez). Kendi bilgisayarınızdan **SSH tüneli** açın:

```bash
ssh -L 3001:127.0.0.1:3001 deploy@<SUNUCU_IP>
```

Tünel açıkken tarayıcıda `http://localhost:3001` adresine gidin. İlk açılışta bir yönetici hesabı oluşturun (bu hesap uygulamanın
hesabından bağımsızdır).

**Monitör ekleme:** "Add New Monitor":
1. Monitor Type: `HTTP(s)`; Friendly Name: `StockAccount canlı`
2. URL: `https://stok.firmaniz.com/api/health/ready` (DNS + sertifika + Caddy + nginx + API + SQL Server + Redis zincirinin tamamını sınar)
3. Heartbeat Interval: `60` sn, Retries: `2`. "Certificate Expiry Notification" kutusunu işaretleyin.
4. İkinci bir monitör (iç ağdan, internet/DNS sorunlarından bağımsız): URL `http://api:8080/health/ready`, Friendly Name: `API (iç)`.

**Bildirim (e-posta / Telegram):** Settings -> Notifications -> "Setup Notification":
- *E-posta (SMTP)*: SMTP sunucusu, port, kullanıcı/parola ve alıcı adresi girin.
- *Telegram*: Telegram'da `@BotFather` ile bot oluşturup token'ı alın; botunuza bir mesaj yazın ve chat ID'nizi
  (`https://api.telegram.org/bot<TOKEN>/getUpdates` yanıtında `chat.id`) girin.
- "Default enabled" ve "Apply on all existing monitors" kutularını işaretleyin, "Test" ile deneyin.

> Uptime Kuma aynı sunucuda çalıştığından, sunucu tamamen kapanırsa kendisi de haber veremez. Ek olarak ücretsiz bir dış
> servisten (ör. UptimeRobot, Better Stack) `https://stok.firmaniz.com/api/health/ready` adresini izletmeniz önerilir.

## 10. Günlük bakım komutları

```bash
dcp ps                         # servis durumları
dcp logs -f --tail 100 api     # API günlüğü (Ctrl+C ile çık)
dcp logs --tail 100 caddy      # sertifika / HTTPS sorunları
df -h /                        # disk doluluğu
docker system prune -f         # kullanılmayan eski katmanlar (çalışan imajlara ve volume'lara dokunmaz)
```

Eski sürüm imajları rollback içindir; disk dolarsa son 2-3 sürüm dışındakileri `docker image rm stockaccount-api:<eski>` ile silin.

## 11. Sorun giderme

| Belirti | Neden / çözüm |
|---|---|
| Tarayıcı "bağlanılamıyor" / sertifika hatası | DNS A kaydı sunucuya işaret ediyor mu (`dig +short <alan adı>`)? 80 ve 443 açık mı (`sudo ufw status`)? `dcp logs --tail 100 caddy` içinde "challenge" / "rate limit" hatası var mı? Düzelttikten sonra `dcp restart caddy`. |
| Deploy "db-migrate" aşamasında duruyor, mesaj `No Admin user exists and BOOTSTRAP_ADMIN_EMAIL ...` | `.env` içinde `BOOTSTRAP_ADMIN_EMAIL` ve `BOOTSTRAP_ADMIN_PASSWORD` dolu olmalı (`new-secrets.ps1 -AdminEmail ...`). Sonra deploy'u tekrarlayın. |
| `BOOTSTRAP_ADMIN_PASSWORD ... rejected` | Parola kuralı: en az 8 karakter, büyük + küçük harf + rakam. `.env`'de düzeltip tekrar deploy edin. |
| `DOMAIN is required` / `ACME_EMAIL is not set` | `.env` içinde `DOMAIN=` ve `ACME_EMAIL=` doldurulmalı. |
| `Invalid configuration ... placeholder` (API açılmıyor) | `.env` hâlâ örnek değerlerde: `pwsh ./scripts/deploy/new-secrets.ps1` çalıştırın. |
| `did not become ready within ... s` | `dcp logs --tail 200 api` ve `dcp logs --tail 50 db-migrate` bakın. Script otomatik geri alma yapar. |
| `mssql` sağlıklı olmuyor, RAM yetersiz | Sunucu belleği 4 GB'tan az olabilir; `free -h` bakın, swap ekleyin (2.5) veya sunucuyu büyütün. |
| `permission denied` (yedek klasörü) | `chmod 777 ~/StockAccountApp/backups ~/StockAccountApp/backups/prod` |
| Giriş sırasında `429` | Dakikada 10 deneme sınırı; 1 dakika bekleyin. |
| Parolamı unuttum | Giriş ekranından kurtarma yoktur. Başka bir Admin hesabı varsa onunla sıfırlatın; yoksa en son yedekten dönüp yeni bir Admin için destek isteyin. Bu yüzden parolayı parola yöneticisinde saklayın. |
| `docker: permission denied` | `deploy` kullanıcısı `docker` grubunda mı (`groups`)? Çıkıp yeniden girin. |
| Uptime Kuma'ya bağlanılamıyor | SSH tüneli açık mı? `dcp ps` içinde `uptime-kuma` çalışıyor mu? |

## 12. Güvenlik özeti

- Yalnızca 22/80/443 açık; SSH parolasız (anahtar), root girişi kapalı; otomatik güvenlik güncellemeleri açık.
- Veritabanı, Redis, API ve arayüz konteynerleri dış ağa kapalı; API veritabanına yetkisi kısıtlı bir kullanıcıyla bağlanır (`sa` değil).
- Canlıda: Swagger kapalı, demo veri yok, placeholder gizli bilgilerle uygulama açılmaz, istek sınırlaması açık.
- İstemci IP'si zinciri: istemci -> Caddy -> nginx -> API. Yalnızca Caddy'nin sabit IP'sinden gelen `X-Forwarded-For`
  nginx'te, yalnızca nginx'ten gelen API'de dikkate alınır; dışarıdan sahte başlık göndererek IP sınırı atlatılamaz.
- HTTPS zorunlu (HTTP -> HTTPS yönlendirme) ve HSTS açık.
