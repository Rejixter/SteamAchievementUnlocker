# GitHub'da paylaşma

Bu klasör kaynak kod deposudur. Kullanıcıya verilecek hazır uygulama **SteamAchievementUnlocker-win-x64.zip** dosyasıdır.
Kişisel API anahtarı, Steam hesap dosyaları, `.vs`, `bin` ve `obj` kaynak ZIP'ine dahil edilmez.

## İlk yükleme

1. GitHub'da boş bir depo oluştur: örneğin `SteamAchievementUnlocker`.
2. Bu temiz kaynak klasörünü GitHub Desktop ile **Add local repository** üzerinden ekle.
   Henüz Git deposu değilse arayüzden **create a repository here** seçeneğini kullan.
3. Değişiklik listesinde yalnızca proje kaynakları, `lib` bağımlılıkları, belgeler,
   testler ve `.github/workflows/release.yml` olduğunu kontrol edip commit oluştur.
4. **Publish repository** ile yükle. Herkese açık paylaşım istiyorsan private seçimini kaldır.

Eski proje klasörünü veya ilk gönderdiğin ZIP'i yükleme; içinde kişisel anahtar dosyası vardı.
Bu klasördeki `.gitignore` kişisel dosyaları ayrıca dışlar. Daha önce Git'e kaydedilmiş bir
dosyayı `.gitignore` geçmişten silmez; bu yüzden temiz klasörden yeni depo oluştur.

## Kullanıcıların doğrudan çalıştıracağı Release

İki yöntemden birini seç:

### Hazır ZIP'i elle eklemek

GitHub'da **Releases → Draft a new release** aç. Yeni bir etiket (örneğin `v1.2.0`) oluştur.
Hazırlanan **SteamAchievementUnlocker-win-x64.zip** ve **SHA256SUMS.txt** dosyalarını ekle,
istersen **SteamAchievementUnlocker-source.zip** dosyasını da ekleyip yayınla.
Kullanıcılara bu Release sayfasını gönder; **Code → Download ZIP** kaynak kod içindir.

### Sonraki sürümleri otomatik hazırlamak

Depoda Actions açıkken bir sürüm etiketi gönder:

```powershell
git tag v1.2.1
git push origin v1.2.1
```

İş akışı testleri çalıştırır, .NET içeren Windows ZIP'ini üretir ve etiket için GitHub Release oluşturur.
Her sürümde yeni etiket kullan. Daha önce elle yayınladığın etiket için aynı işlemi tekrar çalıştırma.
Normal commit ve pull request'lerde yalnızca test/derleme yapılır; ZIP'ler Actions artifacts bölümünde oluşur.
API anahtarı veya ayrıca bir GitHub kişisel erişim anahtarı eklemek gerekmez; iş akışı GitHub'ın otomatik token'ını kullanır.

## Yerel paket üretmek

Windows ve .NET 8 SDK ile proje klasöründe:

```powershell
./scripts/Publish-Release.ps1
```

Çıktılar `artifacts/` klasörüne gelir. Bu klasör Git tarafından dışlanır.
Son kullanıcı .NET SDK kurmaz; çalışma zamanı Windows ZIP'inin içindedir.
