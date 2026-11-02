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
│  │  ├─ CurseForge/            CurseForge modları: anahtar yoksa FTB API'si üzerinden (FtbCurseClient), varsa resmi API
│  │  └─ AllSourcesProvider     "Hepsi": Modrinth + CurseForge'da birlikte arar (modlarda varsayılan)
│  ├─ Profiles/                 GameProfile (+ içerik manifestosu), ProfileStore (kalıcı), eski Masaüstü profillerini içe aktarma
│  ├─ Install/                  DownloadManager, ModpackInstaller, ContentInstaller, ProfileContentService, ProfileDoctor
│  ├─ Mods/                     Siteden bağımsız mod uyumluluğu (jar'ın içindeki bilgiye bakar):
│  │                            JarInspector (fabric.mod.json / quilt.mod.json / mods.toml / neoforge.mods.toml okur),
│  │                            CompatibilityChecker (yanlış loader/sürüm, çift mod, eksik gereksinim — ağa çıkmaz),
│  │                            ModCompatibilityService (oyundan önce otomatik düzeltir), DependencyFinder (mod kimliğinden
│  │                            modu bulup kurar), ContentImporter (sürükle-bırak: her türlü dosyayı doğru yere koyar)
│  ├─ Launch/                   LoaderInstaller (Vanilla/Fabric/Quilt/Forge/NeoForge), GameLauncher, RamAdvisor
│  └─ Auth/                     Microsoft + çevrimdışı giriş
├─ MinecraftModder/             WPF arayüzü
│  ├─ Theme.xaml                Renkler, stiller ve kart şablonları (ModCardTemplate, LookCardTemplate)
│  ├─ MainWindow                Giriş, alt bar (3 sekme | Oyna bloğu | 3 sekme), bilgi balonu, soru kutusu, IShell
│  ├─ Views/                    Sayfalar: Keşfet, Modlar/Paketler/Görünüm (BrowsePage), Disklerim, Ayarlar,
│  │                            disk detayı ve yeni disk (üstte açılan paneller). Sayfalar sadece IShell'i bilir.
│  ├─ Controls/                 DiscControl (profil = CD, dilimlerde mod ikonları), PlayBlock (izometrik Oyna bloğu)
│  └─ PixelArt.cs               Kodla üretilen piksel desenler (oyunun dokuları kopyalanmaz)
└─ MinecraftModder.Core.Tests/  xUnit testleri (ağa çıkmaz)
```

## Kurallar

- **Her modpack kendi yeni profiline kurulur.** Mevcut profile asla modpack yazılmaz; sürüm/loader karışmaz.
- **Sürüm ve loader her zaman profilden okunur** (`profile.GameVersion`, `profile.Loader`); kodda sabit sürüm yazılmaz.
- **Ağ istekleri `LauncherHttp.Shared` ile yapılır** (IPv4 zorlamalı, bağlantı havuzlu). `new HttpClient()` açılmaz.
- **Dışarıdan gelen her dosya yolu `SafePath.Combine` ile çözülür** (zip-slip koruması). Paket yolları güvenilmezdir.
- **İndirmeler `DownloadManager` üzerinden** yapılır: hash doğrulama, yedek adresler, paralel indirme, `.part` dosyası.
- Bir profile dosya kuran her kod `profile.Content` manifestosunu da günceller (Onar, paylaşım kodu ve arayüz buna dayanır).
- **Modun hangi siteden geldiği önemsizdir.** Aynı modu tanımak için `ContentEntry.ModId` (jar'ın kendi kimliği) kullanılır;
  sağlayıcı+proje kimliği sadece indirme/güncelleme içindir. Bir mod başka siteden zaten kuruluysa ikincisi kurulmaz.
- Oyun her açılmadan önce `ModCompatibilityService.CheckAndFixAsync` çalışır. Mod paketinin kendi dosyalarına
  (`ContentOrigin.Pack/PackOverride`) dokunmaz, sadece bildirir; kullanıcının ve launcher'ın kurduklarını düzeltir.
- `libraries/assets/versions/runtime` tüm profillerde ortaktır (`shared\`); oyun klasörü (`--gameDir`) profile özeldir.
- Kullanıcıya gösterilen metinler Türkçe ve teknik terimsiz olmalı. Arayüzde profile "disk" denir.
- Arayüzde MessageBox kullanılmaz; `IShell.AskAsync` (temalı soru kutusu) ve `IShell.Notify` (bilgi balonu) kullanılır.

## Komutlar

```bash
dotnet build MinecraftModder.sln
dotnet test MinecraftModder.Core.Tests
```

Gerçek profillere dokunmadan denemek için veri klasörü değiştirilebilir: `MINECRAFTMODDER_DATA=<klasör>`.

## Sıradaki adımlar

- Disk tıklanınca Oyna bloğuna "uçarak" takılma animasyonu; mod kartına tıklayınca açılan detay (açıklama, görseller, yorumlar)
- Yönergeli ilk kurulum (arkadaş kodu var mı? → ne oynamak istersin?), dil seçimi, geri bildirim alanı
- Arkadaş kodu: profil manifestosunu (`GameProfile.Content` + `Source`) bir sunucuya yükleyip kısa kodla paylaşma
- Technic paketleri (Hexxit vb.): eski `bin/modpack.jar` biçimi; ayrı bir sağlayıcı gerektirir
- Yerel CurseForge paket zip'i (manifest.json) içe aktarma: dosya numaraları FTB'nin `mod/{id}` sürüm listesinden çözülebilir
- FTB'nin parmak izi adresi (`mod/lookup/{murmur}`) "eskidi" uyarısı veriyor; kalkarsa sadece elle atılan CurseForge jar'larının adı/ikonu çıkmaz
- Shader karşılaştırma sekmesi, Keşfet (günlük öneriler), favoriler (`AppSettings.FavoriteKeys` hazır)
