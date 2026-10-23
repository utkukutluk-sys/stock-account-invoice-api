[![CI](https://github.com/utkukutluk-sys/stock-account-invoice-api/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/utkukutluk-sys/stock-account-invoice-api/actions/workflows/ci.yml)

# Stok, Cari ve Fiş Yönetimi — Web API + UI

Stok, Cari ve Fiş (fatura) hizmetleri sunan bir **.NET 10 Web API** ve bu API'yi kullanan iki ön yüz:

- **`ui-vue/`**: Vue 3, Vite, PrimeVue ve PrimeFlex ile yazılmış SPA.
- **`ui-nuxt/`**: Aynı uygulamanın Nuxt 4'e taşınmış, SSR'lı sürümü (opsiyonel madde).

Bütün sistem Docker Compose ile tek komutla ayağa kalkar (opsiyonel madde).

| Katman | Teknoloji |
|---|---|
| API | .NET 10, ASP.NET Core, EF Core 10 (Code-First + migration), ASP.NET Identity + JWT |
| Veritabanı | Microsoft SQL Server |
| Cache | Redis. Erişilemezse otomatik olarak bellek içi cache'e geçer (circuit breaker) |
| Loglama | Serilog: console, günlük dönen dosya, request log, correlation id |
| Dokümantasyon / test | Swagger (OpenAPI), Postman koleksiyonu, xUnit, Vitest, Playwright |
| UI | Vue 3 + PrimeVue 4 + PrimeFlex, Nuxt 4 (SSR), Pinia |
| Konteynır | Docker, docker-compose (mssql, redis, api, ui-vue, ui-nuxt) |

---

## Hızlı başlangıç (Docker)

```powershell
.\scripts\deploy\new-secrets.ps1     # .env dosyasını güçlü, rastgele şifre ve anahtarlarla oluşturur
docker compose up --build -d
```

`.env` içinde bir değer eksikse `docker compose` başlamaz.

| Servis | Adres |
|---|---|
| Vue arayüzü | http://localhost:5173 |
| Nuxt arayüzü (SSR) | http://localhost:3000 |
| API + Swagger | http://localhost:5080/swagger |
| Health | http://localhost:5080/health |
| SQL Server | `localhost,14333` (kullanıcı `sa`, şifre `.env` içinde) |

İlk açılışta tek seferlik `db-migrate` servisi migration'ları uygular ve örnek verileri yükler. API, SQL Server ve Redis portları yalnızca bu bilgisayardan (127.0.0.1) erişilebilir.

### Demo kullanıcılar

| E-posta | Şifre | Rol |
|---|---|---|
| admin@stockaccount.local | Admin123! | **Admin**: tüm şirketleri yönetir; şirket seçimi üst bardan yapılır |
| demo@stockaccount.local | Demo123! | **User**: "Demo Gıda A.Ş." şirketinin operatörü |
| ahmet@stockaccount.local | Ahmet123! | **User (cari portalı)**: C001 Ahmet Yılmaz carisine bağlı; yalnız kendi fişlerini ve bakiyesini görür |

## Yerel geliştirme (Docker'sız)

Gereksinimler: .NET SDK 10, SQL Server (varsayılan `.\SQLEXPRESS`, Windows auth), Node 24. Redis isteğe bağlıdır.

```powershell
docker run -d --name sa-redis -p 6379:6379 redis:7-alpine   # isteğe bağlı
dotnet tool restore
dotnet run --project src/StockAccount.Api                    # http://localhost:5080

cd ui-vue;  npm ci; npm run dev                              # http://localhost:5173
cd ui-nuxt; npm ci; npm run dev                              # http://localhost:3000
```

İki arayüz de `/api` isteklerini API'ye proxy'ler. Böylece refresh cookie aynı origin üzerinden taşınır.

> Docker Compose'u çalıştırmadan önce `sa-redis` konteynırını durdurun (`docker stop sa-redis`), ikisi de 6379 portunu kullanır.

---

## Gereksinimlerin karşılanması

| Ödev maddesi | Karşılandığı yer |
|---|---|
| Admin / User yetkilendirmesi | Identity + JWT. Politikalar: `AdminOnly`, `Operator`, `Portal`. Access token yalnızca bellekte tutulur; refresh token httpOnly + SameSite=Strict cookie'dedir, her kullanımda yenilenir ve tekrar kullanılırsa iptal edilir |
| Şirket ana büyüklük; şirket bir kullanıcı olabilir | Tüm kayıtlar `CompanyId` ile şirkete bağlıdır, kullanıcılar bir şirkete aittir. Admin `X-Company-Id` header'ı ile şirket seçer |
| Cari bir kullanıcı olabilir | Kullanıcı bir cariye bağlanabilir ve cari portalını (`/api/me/*`) kullanır |
| Stok, Cari, Fiş ve Şirket/Kullanıcı CRUD | Tüm büyüklükler için REST endpoint'leri ve arayüz ekranları |
| Listelerde sayfalama ve filtreleme | `page`, `pageSize` (en fazla 100), `sort` ve büyüklüğe özel filtreler. Sıralama alanları whitelist ile sınırlıdır, SQL injection'a kapalıdır |
| Stok ve cari için cache, CRUD ile otomatik yenilenmesi | Redis'te sürüm numaralı liste anahtarları kullanılır. Ekleme, güncelleme, silme ve fiş onay/iptal işlemlerinde ilgili cache temizlenir. Yanıtta `X-Cache: HIT/MISS` header'ı döner |
| UI'da HTTP isteklerinin cache'lenmesi | Vue: URL önekine göre TTL'li LRU cache, aynı anda giden aynı isteklerin birleştirilmesi, ETag/304 ve değişiklik sonrası temizleme. Nuxt: `useAsyncData` + `getCachedData` |
| Fiş: başlık (cari, tarih, seri) + detay (stok) | `Invoice` + `InvoiceLine`. Satış ve alış tipleri, satır bazında KDV (%0/1/10/20) |
| İlk oluşturulduğunda taslak | Durumlar: `Draft` → `Approved` → `Cancelled`. Yalnız taslak fiş düzenlenip silinebilir |
| Onay ile cari ve stok hareketleri | Onay tek transaction'da yapılır: StockTrans ve ActTrans kayıtları yazılır, saklanan bakiyeler güncellenir, fiş numarası verilir (ör. SAT000001) |
| Balance değerleri stok ve cari kayıtlarında | `Stock.Balance` ve `CurrentAccount.Balance` alanları. Arka plandaki mutabakat servisi bu değerleri hareket toplamlarıyla periyodik olarak karşılaştırır |
| Serilog | Console + dosya log, request log; log satırlarında kullanıcı, şirket ve correlation id bulunur |
| Swagger ve Postman | `/swagger` (JWT "Authorize" butonu ile), `postman/` altında koleksiyon ve iki ortam dosyası |
| (Opsiyonel) Nuxt'a geçiş | `ui-nuxt/`: SSR açık; refresh cookie sunucu tarafında iletilir, sayfalar veriyle birlikte render edilir |
| (Opsiyonel) Docker | API ve iki UI için Dockerfile, `docker-compose.yml` ve health check'ler |

### Örnek senaryo: Ahmet'e 2 kg Elma satışı

1. Ahmet carisine satış fişi oluşturulur: 2 kg ELMA × 50,00 TL, %1 KDV. Fiş **taslak** durumdadır, toplam **101,00 TL**'dir.
2. Fiş **onaylanır** ve SAT000001 numarasını alır.
   - **StockTrans:** ELMA için 2 kg **çıkış** (tüketim) kaydı yazılır.
   - **ActTrans:** Ahmet için **101,00 TL borç** kaydı yazılır.
   - Saklanan bakiyeler güncellenir: ELMA 100 → 98, Ahmet 0 → 101,00.
3. Fiş iptal edilirse (yalnız Admin) ters kayıtlar yazılır ve bakiyeler eski haline döner. Hareket tabloları değiştirilemez ve silinemez; bunu veritabanındaki trigger'lar garanti eder.

Bu senaryo xUnit entegrasyon testlerinde, Postman koleksiyonunda ve Playwright E2E testlerinde otomatik olarak doğrulanır.

---

## Mimari

```
src/
  StockAccount.Domain          entity'ler, enum'lar, hesaplama ve iş kuralları
  StockAccount.Application     servisler, DTO'lar, FluentValidation, cache ve yetki soyutlamaları
  StockAccount.Infrastructure  EF Core (DbContext, konfigürasyonlar, migration'lar, seed), Identity/JWT,
                               Redis cache + circuit breaker, mutabakat background service
  StockAccount.Api             controller'lar, ProblemDetails hata yönetimi, ETag, Serilog, Swagger, health
tests/
  StockAccount.UnitTests        domain hesapları, onay/iptal, validator'lar, cache, circuit breaker
  StockAccount.IntegrationTests WebApplicationFactory; her koşuda yeni bir SQL Server veritabanı
ui-vue/   ui-nuxt/   e2e/ (Playwright)   postman/   docs/
```

Öne çıkan kararlar:

- **Veri bütünlüğü:**
  - Finansal ilişkilerde FK'ler `Restrict` ile tanımlıdır.
  - 26 CHECK constraint vardır.
  - Silinmiş kayıtları hariç tutan filtreli unique index'ler kullanılır.
  - Eşzamanlılık `rowversion` ile kontrol edilir.
  - Fiş numarası (şirket, seri, numara) üzerindeki unique index ile korunur.
  - Onay işlemi, çakışmada 3 kereye kadar yeniden denenir. Aynı stoğa paralel onaylarda bile stok eksiye düşmez.
- **Silme:** Şirket, stok ve cari soft delete ile silinir. Bakiyesi olan ya da bir fişte kullanılan kayıt silinemez.
- **Hata formatı:** Tüm hatalar `application/problem+json` formatında, iş kodu ile döner (`INSUFFICIENT_STOCK`, `INVOICE_NOT_DRAFT` vb.).

Ayrıntılı tasarım belgeleri:

- [docs/CONTRACT.md](docs/CONTRACT.md): API sözleşmesi, veri modeli, iş kuralları
- [docs/DB_NOTES.md](docs/DB_NOTES.md): veritabanı tasarım notları
- [docs/BACKEND_NOTES.md](docs/BACKEND_NOTES.md): backend tasarım notları
- [docs/FRONTEND_NOTES.md](docs/FRONTEND_NOTES.md): frontend tasarım notları

## Canlıya alma ve operasyon

### Production hazırlık kontrol listesi

| Kontrol | Nasıl karşılandı |
|---|---|
| Tüm API uçlarında rate limiting | Her uçta kullanıcı başına, giriş yapılmamışsa IP başına limit var. Giriş ve token yenileme uçlarında IP başına daha sıkı limit uygulanır. Limit aşılınca `429` (problem+json, `RATE_LIMITED`, `Retry-After`) döner. Gerçek istemci IP'si yalnızca iki UI proxy'sinden kabul edilir; doğrudan istekte sahte `X-Forwarded-For` işe yaramaz |
| Açılışta ortam değişkeni doğrulama | Eksik ya da örnek değerli ayarda (JWT anahtarı, bağlantı cümlesi, Redis, `sa` ile runtime bağlantısı, herkese açık bir ağa güven) API açılmaz; tek bir log satırı yazıp exit 2 ile çıkar. Nuxt, `NUXT_API_BASE` olmadan açılmaz |
| Global error handler, stack trace göstermeme | Tüm hatalar `application/problem+json` formatında döner, buna 404/405/415/500 de dahildir. Production'da stack trace, tip adı ve iç mesaj gösterilmez. Güvenlik header'ları ve HSTS açıktır. Nuxt'ta Türkçe, iç bilgi göstermeyen bir hata sayfası vardır |
| Bağlantı havuzu ve health check | Havuz ayarları açıkça tanımlıdır (Max/Min Pool, Application Name). Health uçları: `/health/live` (yalnızca süreç) ve `/health/ready` (SQL + Redis; Redis yoksa `Degraded`). Docker healthcheck'leri bu uçlara bağlıdır. API, yetkisi kısıtlı `stockaccount_app` kullanıcısıyla çalışır |
| Tek komutla geri dönüş, Docker ve yedekleme | Sürümlü imajlar, dağıtım öncesi otomatik yedek, sağlık kontrolü başarısız olursa otomatik geri dönüş ve tek komutluk `rollback.ps1` (aşağıda) |

### Dağıtım ve geri dönüş

```powershell
.\scripts\deploy\deploy.ps1 -Version 1.6.0           # önce yedek alır, sonra derler ve dağıtır; sağlıksızsa kendiliğinden geri döner
.\scripts\deploy\rollback.ps1                        # bir önceki sürüme döner
.\scripts\deploy\rollback.ps1 -RestoreDatabase       # ...ve veritabanını dağıtım öncesi yedekten geri yükler
```

- Docker ağ yapısı değişen sürümler kendiliğinden algılanır ve ortam volume'lar korunarak yeniden kurulur.
- Şema geri dönüşü EF down-migration ile değil, dağıtım öncesi yedeğin geri yüklenmesiyle yapılır.

### Yedekleme

```powershell
.\scripts\db\backup.ps1 -Target docker -Label manual          # checksum'lı yedek + manifest + RESTORE VERIFYONLY
.\scripts\db\restore.ps1 -Target docker -Latest -Force        # geri yükler, CHECKDB yapar, Redis cache'ini temizler
.\scripts\db\register-backup-task.ps1 -Target docker -At 02:00 -RetentionCount 14   # günlük zamanlanmış yedek
```

Yedekler `./backups` klasöründe tutulur. Ayrıntılar:

- [docs/BACKEND_NOTES.md](docs/BACKEND_NOTES.md) §7
- [docs/DB_NOTES.md](docs/DB_NOTES.md) §8

## Testler

| Test seti | Komut | Sonuç |
|---|---|---|
| Birim (.NET) | `dotnet test tests/StockAccount.UnitTests` | 99 / 99 |
| Entegrasyon (.NET) | `dotnet test tests/StockAccount.IntegrationTests` | 179 / 179 |
| Birim (ui-vue / ui-nuxt) | `npm test` | 85 / 85 ve 73 / 73 |
| Production hazırlık senaryoları | `tests/prod-readiness/*.sh` | Rate limit, ayar doğrulama, hata sızıntısı, health, geri dönüş tatbikatları |
| Postman (newman) | `cd postman; npm test` | 115 istek, 350 doğrulama, 0 hata |
| E2E (Playwright: Vue, Nuxt ve ikisinin mobil görünümü) | `cd e2e; npm run test:vue` / `npm run test:nuxt` | 56 geçti, 2 atlandı (yalnız Nuxt'a ait testler), 0 hata |

Test kapsamı, bulunan ve düzeltilen hatalar ve gereksinim izlenebilirliği: [docs/TEST_REPORT.md](docs/TEST_REPORT.md).
