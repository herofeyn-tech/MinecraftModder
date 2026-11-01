# MinecraftModder

Hiç modlama bilmeyen oyuncular için ("kuzen-dostu") hızlı, yormayan bir Minecraft mod launcher'ı. C# / .NET 8 / WPF, CmlLib.Core 4.

Tasarım notları ve rakip ekran görüntüleri depoda değil: `Desktop\MinecraftModder\` (Genel Bilinç akışı.md, Minecraft Moder fotolar\).

## Yapı

```
MinecraftModder.sln
├─ MinecraftModder.Core/        Tüm iş mantığı. WPF'e bağımlı DEĞİL; arayüz değişse de dokunulmaz.
│  ├─ LauncherCore.cs           Tek giriş noktası: tüm servisleri oluşturur. Arayüz sadece bunu kullanır.
│  ├─ AppPaths.cs               %AppData%\MinecraftModder\{settings.json, profiles\, instances\, shared\, temp\}
│  ├─ Content/                  İçerik kaynakları (IContentProvider / IModpackProvider)
│  │  ├─ Modrinth/              mod, modpack (.mrpack), shader, kaynak paketi — anahtarsız
│  │  ├─ Ftb/                   FTB paketleri + CurseForge MODPACK'leri (FTB API'si üzerinden, anahtarsız)
│  │  └─ CurseForge/            CurseForge modları — Ayarlar'da API anahtarı gerekir
│  ├─ Profiles/                 GameProfile (+ içerik manifestosu), ProfileStore (kalıcı), eski Masaüstü profillerini içe aktarma
│  ├─ Install/                  DownloadManager, ModpackInstaller, ContentInstaller, ProfileContentService, ProfileDoctor
│  ├─ Launch/                   LoaderInstaller (Vanilla/Fabric/Quilt/Forge/NeoForge), GameLauncher, RamAdvisor
│  └─ Auth/                     Microsoft + çevrimdışı giriş
├─ MinecraftModder/             WPF kabuğu (geçici arayüz; yeniden tasarlanacak)
└─ MinecraftModder.Core.Tests/  xUnit testleri (ağa çıkmaz)
```

## Kurallar

- **Her modpack kendi yeni profiline kurulur.** Mevcut profile asla modpack yazılmaz; sürüm/loader karışmaz.
- **Sürüm ve loader her zaman profilden okunur** (`profile.GameVersion`, `profile.Loader`); kodda sabit sürüm yazılmaz.
- **Ağ istekleri `LauncherHttp.Shared` ile yapılır** (IPv4 zorlamalı, bağlantı havuzlu). `new HttpClient()` açılmaz.
- **Dışarıdan gelen her dosya yolu `SafePath.Combine` ile çözülür** (zip-slip koruması). Paket yolları güvenilmezdir.
- **İndirmeler `DownloadManager` üzerinden** yapılır: hash doğrulama, yedek adresler, paralel indirme, `.part` dosyası.
- Bir profile dosya kuran her kod `profile.Content` manifestosunu da günceller (Onar, paylaşım kodu ve arayüz buna dayanır).
- `libraries/assets/versions/runtime` tüm profillerde ortaktır (`shared\`); oyun klasörü (`--gameDir`) profile özeldir.
- Kullanıcıya gösterilen metinler Türkçe ve teknik terimsiz olmalı.

## Komutlar

```bash
dotnet build MinecraftModder.sln
dotnet test MinecraftModder.Core.Tests
```

## Sıradaki adımlar

- Yeni arayüz (CD/disk profiller, alt bar + ortada Oyna bloğu, dikey mod kartları)
- Arkadaş kodu: profil manifestosunu (`GameProfile.Content` + `Source`) bir sunucuya yükleyip kısa kodla paylaşma
- Technic paketleri (Hexxit vb.): eski `bin/modpack.jar` biçimi; ayrı bir sağlayıcı gerektirir
- Shader karşılaştırma sekmesi, Keşfet (günlük öneriler), favoriler (`AppSettings.FavoriteKeys` hazır)
