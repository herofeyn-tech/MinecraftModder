using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft; // Xbox Girişi İçin Eklendi
using CmlLib.Core.ModLoaders.FabricMC;

namespace MinecraftModder
{
    public partial class MainWindow : Window
    {
        private const string GameVersion = "1.20.1";
        private readonly MinecraftPath _gamePath;
        private List<ModrinthHit> _currentHits = new List<ModrinthHit>();
        private string? _preferredFabricLoaderVersion;

        // UI geçişleri sırasında güncel açık olan paketi takip etmek için
        private InstalledModpackGroup? _currentOpenGroup;
        private List<IInstalledItem> _lastLoadedItems = new();

        // OTURUM BİLGİSİNİ TUTACAK DEĞİŞKEN
        private MSession? _currentSession;

        // MICROSOFT GİRİŞ YÖNETİCİSİ (Beni Hatırla için)
        private JELoginHandler _loginHandler;

        // ==================== PERFORMANS DÜZELTMESİ ====================
        // Önceden her HTTP isteği için "new HttpClient()" oluşturuluyordu. Bu, her istekte
        // TCP+TLS el sıkışmasını sıfırdan yaptırıp bağlantı havuzunu (connection pooling)
        // devre dışı bırakan bilinen bir .NET performans hatasıdır. Artık uygulama boyunca
        // TEK bir HttpClient paylaşılıyor. Ayrıca zaman aşımını 20 saniyeye indirdik:
        // ağda gerçek bir sorun varsa uygulama 100 saniye boyunca donmak yerine hızlıca hata verir.
        //
        // IPv6 DÜZELTMESİ: Bazı kullanıcıların ağında (bozuk IPv6 yönlendirmesi / MTU sorunu)
        // Windows önce IPv6 ile bağlanmayı dener, başarısız olur, sonra IPv4'e düşer — bu geçiş
        // uzun bir gecikmeye (bizim durumumuzda 20 saniyelik timeout'a çarpmaya) sebep olabiliyor.
        // Bunu her kullanıcının kendi ağ ayarlarından IPv6'yı kapatmasına gerek kalmadan, uygulama
        // seviyesinde çözüyoruz: ConnectCallback ile DNS'den dönen adresler arasından SADECE IPv4
        // olanlarını deniyoruz, IPv6'yı hiç denemiyoruz. Böylece "önce dene, sonra vazgeç" gecikmesi
        // tamamen ortadan kalkıyor.
        private static readonly HttpClient _httpClient = CreateHttpClient();

        // AsyncImageLoader gibi sınıfların da aynı (IPv4-zorlayan, hızlı) HttpClient'ı
        // kullanabilmesi için dışarıya açık bir erişim noktası.
        public static HttpClient SharedHttpClient => _httpClient;

        private static HttpClient CreateHttpClient()
        {
            var socketsHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
                    var ipv4Addresses = entry.AddressList
                        .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                        .ToArray();

                    // Bu makinede gerçekten sadece IPv6 varsa (çok nadir), IPv4'e zorlamak yerine
                    // normal (IPv6 dahil) bağlantıya izin veriyoruz; yoksa internet hiç çalışmaz.
                    if (ipv4Addresses.Length == 0)
                    {
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        socket.NoDelay = true;
                        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }

                    Exception? lastError = null;
                    foreach (var ip in ipv4Addresses)
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        socket.NoDelay = true;
                        try
                        {
                            await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            socket.Dispose();
                        }
                    }

                    throw lastError ?? new InvalidOperationException($"{context.DnsEndPoint.Host} adresine bağlanılamadı.");
                }
            };

            var client = new HttpClient(socketsHandler);
            client.DefaultRequestHeaders.Add("User-Agent", "MinecraftModderApp/1.0");
            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        }

        public MainWindow()
        {
            InitializeComponent();
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string targetFolder = Path.Combine(desktopPath, "Oyun Test", ".minecraft_test");
            _gamePath = new MinecraftPath(targetFolder);

            // Login Handler'ı oluştur
            _loginHandler = JELoginHandlerBuilder.BuildDefault();
        }

        // ==================== GİRİŞ EKRANI İŞLEMLERİ ====================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            XboxLoginStatus.Text = "Kayıtlı oturum kontrol ediliyor...";
            try
            {
                var session = await _loginHandler.AuthenticateSilently();
                if (session != null && session.CheckIsValid())
                {
                    _currentSession = session;

                    XboxLoggedOutPanel.Visibility = Visibility.Collapsed;
                    XboxLoggedInPanel.Visibility = Visibility.Visible;
                    XboxRememberedUsername.Text = $"Kayıtlı Hesap: {session.Username}";
                    XboxLoginStatus.Text = "Önceki oturum başarıyla bulundu!";
                }
                else
                {
                    XboxLoginStatus.Text = "";
                }
            }
            catch
            {
                XboxLoginStatus.Text = "";
            }
        }

        private async void XboxLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            XboxLoginBtn.IsEnabled = false;
            XboxLoginStatus.Text = "Tarayıcıda Microsoft ekranı bekleniyor...";

            try
            {
                var session = await _loginHandler.AuthenticateInteractively();
                _currentSession = session;

                XboxLoggedOutPanel.Visibility = Visibility.Collapsed;
                XboxLoggedInPanel.Visibility = Visibility.Visible;
                XboxRememberedUsername.Text = $"Kayıtlı Hesap: {session.Username}";

                WelcomeText.Text = $"Hoş Geldin, {session.Username}!";
                SwitchToMainScreen();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Xbox girişi başarısız oldu: {ex.Message}", "Giriş Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                XboxLoginStatus.Text = "Giriş yapılamadı.";
            }
            finally
            {
                XboxLoginBtn.IsEnabled = true;
            }
        }

        private void XboxContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null)
            {
                WelcomeText.Text = $"Hoş Geldin, {_currentSession.Username}!";
                SwitchToMainScreen();
            }
        }

        private void XboxLogoutBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _loginHandler.Signout();
            }
            catch { }

            _currentSession = null;
            XboxLoggedInPanel.Visibility = Visibility.Collapsed;
            XboxLoggedOutPanel.Visibility = Visibility.Visible;
            XboxLoginStatus.Text = "Hesap sistemden unutuldu.";
        }

        private void OfflineLoginBtn_Click(object sender, RoutedEventArgs e)
        {
            string username = OfflineUsernameBox.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Lütfen bir kullanıcı adı girin!", "Uyarı");
                return;
            }

            _currentSession = MSession.CreateOfflineSession(username);
            WelcomeText.Text = $"Hoş Geldin, {username}!";
            SwitchToMainScreen();
        }

        private void SwitchToMainScreen()
        {
            LoginScreen.Visibility = Visibility.Collapsed;
            MainScreen.Visibility = Visibility.Visible;
        }

        // ==================== ARAMA VE İNDİRME İŞLEMLERİ ====================

        private async void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                MessageBox.Show("Lütfen aranacak bir mod adı yazın!", "Uyarı");
                return;
            }

            StatusText.Text = "Modrinth üzerinde aranıyor...";
            ModListBox.ItemsSource = null;
            _currentHits.Clear();

            try
            {
                string facets = Uri.EscapeDataString($"[[\"categories:fabric\"],[\"versions:{GameVersion}\"],[\"project_type:mod\",\"project_type:modpack\"]]");
                string url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&limit=10&facets={facets}";
                string response = await _httpClient.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<ModrinthSearchResponse>(response);

                if (result?.hits == null || result.hits.Count == 0)
                {
                    StatusText.Text = "Bu isimde / bu sürümle uyumlu mod bulunamadı.";
                    return;
                }

                _currentHits = result.hits;
                StatusText.Text = $"{result.hits.Count} sonuç bulundu! İndirmek için çift tıkla.";
                ModListBox.ItemsSource = _currentHits;
            }
            catch (TaskCanceledException)
            {
                // Timeout süresi (20 sn) dolduğunda buraya düşer. Muhtemelen ağ/DNS sorunu var.
                MessageBox.Show("İstek zaman aşımına uğradı. İnternet bağlantını ya da güvenlik duvarı/antivirüs ayarlarını kontrol et.", "Zaman Aşımı");
                StatusText.Text = "Arama zaman aşımına uğradı.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Arama hatası: {ex.Message}", "Hata");
                StatusText.Text = "Arama başarısız.";
            }
        }

        private async void ModListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ModListBox.SelectedItem is ModrinthHit hit)
            {
                if (hit.project_type == "modpack")
                    await InstallModpackAsync(hit);
                else
                    await DownloadModAsync(hit);
            }
        }

        private async Task DownloadModAsync(ModrinthHit hit)
        {
            try
            {
                string modsFolder = Path.Combine(_gamePath.BasePath, "mods");
                Directory.CreateDirectory(modsFolder);

                var processedProjects = new HashSet<string>();
                var downloaded = new List<string>();
                var skipped = new List<string>();

                await DownloadProjectRecursiveAsync(hit.project_id, hit.title, modsFolder, processedProjects, downloaded, skipped);

                string summary = $"İndirilenler:\n- {string.Join("\n- ", downloaded)}";
                if (skipped.Count > 0) summary += $"\n\nUyumsuzluk sebebiyle atlananlar:\n- {string.Join("\n- ", skipped)}";

                StatusText.Text = $"{hit.title} ve bağımlılıkları indirildi!";
                MessageBox.Show(summary, "İndirme Tamamlandı");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Mod indirme hatası: {ex.Message}", "Hata");
                StatusText.Text = "Mod indirme başarısız.";
            }
        }

        private async Task DownloadProjectRecursiveAsync(string projectId, string displayName, string modsFolder, HashSet<string> processedProjects, List<string> downloaded, List<string> skipped)
        {
            if (processedProjects.Contains(projectId)) return;
            processedProjects.Add(projectId);
            StatusText.Text = $"{displayName} indiriliyor...";

            string loaders = Uri.EscapeDataString("[\"fabric\"]");
            string gameVersions = Uri.EscapeDataString($"[\"{GameVersion}\"]");
            string versionsUrl = $"https://api.modrinth.com/v2/project/{projectId}/version?loaders={loaders}&game_versions={gameVersions}";

            string versionsJson = await _httpClient.GetStringAsync(versionsUrl);
            var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(versionsJson);

            if (versions == null || versions.Count == 0 || versions[0].files == null || versions[0].files.Count == 0)
            {
                skipped.Add($"{displayName} (Fabric + {GameVersion} uyumlu sürüm yok)");
                return;
            }

            var chosenVersion = versions[0];
            var file = chosenVersion.files.Find(f => f.primary) ?? chosenVersion.files[0];

            if (!file.filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{displayName} (mod jar dosyası değil)");
                return;
            }

            string destPath = Path.Combine(modsFolder, file.filename);
            byte[] data = await _httpClient.GetByteArrayAsync(file.url);
            await File.WriteAllBytesAsync(destPath, data);
            downloaded.Add(displayName);

            if (chosenVersion.dependencies == null) return;

            foreach (var dep in chosenVersion.dependencies)
            {
                if (dep.dependency_type != "required") continue;

                string? depProjectId = dep.project_id;
                if (string.IsNullOrEmpty(depProjectId) && !string.IsNullOrEmpty(dep.version_id))
                {
                    string versionInfoJson = await _httpClient.GetStringAsync($"https://api.modrinth.com/v2/version/{dep.version_id}");
                    var versionInfo = JsonSerializer.Deserialize<ModrinthVersion>(versionInfoJson);
                    depProjectId = versionInfo?.project_id;
                }
                if (string.IsNullOrEmpty(depProjectId)) continue;

                string depName = depProjectId;
                try
                {
                    string projectJson = await _httpClient.GetStringAsync($"https://api.modrinth.com/v2/project/{depProjectId}");
                    var projectInfo = JsonSerializer.Deserialize<ModrinthHit>(projectJson);
                    if (projectInfo != null && !string.IsNullOrEmpty(projectInfo.title)) depName = projectInfo.title;
                }
                catch { }

                await DownloadProjectRecursiveAsync(depProjectId, depName, modsFolder, processedProjects, downloaded, skipped);
            }
        }

        private async Task InstallModpackAsync(ModrinthHit hit)
        {
            string? tempMrpackPath = null;
            try
            {
                StatusText.Text = $"{hit.title} modpack'i indiriliyor...";
                Directory.CreateDirectory(_gamePath.BasePath);

                string loaders = Uri.EscapeDataString("[\"fabric\"]");
                string gameVersions = Uri.EscapeDataString($"[\"{GameVersion}\"]");
                string versionsUrl = $"https://api.modrinth.com/v2/project/{hit.project_id}/version?loaders={loaders}&game_versions={gameVersions}";
                string versionsJson = await _httpClient.GetStringAsync(versionsUrl);
                var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(versionsJson);

                if (versions == null || versions.Count == 0 || versions[0].files.Count == 0)
                {
                    MessageBox.Show($"Uygun modpack sürümü bulunamadı.", "Uyarı");
                    return;
                }

                var chosenVersion = versions[0];
                var mrpackFileInfo = chosenVersion.files.Find(f => f.filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) ?? chosenVersion.files[0];

                byte[] mrpackData = await _httpClient.GetByteArrayAsync(mrpackFileInfo.url);
                tempMrpackPath = Path.Combine(Path.GetTempPath(), $"{hit.slug}_{Guid.NewGuid():N}.mrpack");
                await File.WriteAllBytesAsync(tempMrpackPath, mrpackData);

                using (var archive = ZipFile.OpenRead(tempMrpackPath))
                {
                    var indexEntry = archive.GetEntry("modrinth.index.json");
                    if (indexEntry == null) return;

                    MrpackManifest? manifest;
                    using (var stream = indexEntry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        manifest = JsonSerializer.Deserialize<MrpackManifest>(await reader.ReadToEndAsync());
                    }

                    if (manifest == null || manifest.files.Count == 0) return;

                    int total = manifest.files.Count;
                    int done = 0;
                    List<string> downloadedJars = new List<string>();

                    foreach (var mf in manifest.files)
                    {
                        if (mf.env != null && string.Equals(mf.env.client, "unsupported", StringComparison.OrdinalIgnoreCase)) continue;
                        if (mf.downloads == null || mf.downloads.Count == 0) continue;

                        string destPath = Path.Combine(_gamePath.BasePath, mf.path.Replace('/', Path.DirectorySeparatorChar));
                        string? destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                        byte[] fileData = await _httpClient.GetByteArrayAsync(mf.downloads[0]);
                        await File.WriteAllBytesAsync(destPath, fileData);

                        if (destPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadedJars.Add(Path.GetFileName(destPath));
                        }

                        done++;
                        StatusText.Text = $"Modpack dosyaları... ({done}/{total})";
                        DownloadProgressBar.Maximum = total;
                        DownloadProgressBar.Value = done;
                    }

                    ExtractOverridesFolder(archive, "overrides/", _gamePath.BasePath);
                    ExtractOverridesFolder(archive, "client-overrides/", _gamePath.BasePath);

                    if (manifest.dependencies != null && manifest.dependencies.TryGetValue("fabric-loader", out var loaderVer))
                        _preferredFabricLoaderVersion = loaderVer;

                    string metaFolder = Path.Combine(_gamePath.BasePath, "modpacks_meta");
                    Directory.CreateDirectory(metaFolder);
                    var meta = new InstalledModpackMeta
                    {
                        Title = hit.title,
                        IconUrl = hit.icon_url,
                        InstalledFiles = downloadedJars
                    };
                    string safeTitle = string.Join("_", hit.title.Split(Path.GetInvalidFileNameChars()));
                    string metaPath = Path.Combine(metaFolder, $"{safeTitle}.json");
                    await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                    StatusText.Text = $"{hit.title} modpack'i kuruldu!";
                    MessageBox.Show($"'{hit.title}' kuruldu.\n{done} dosya indirildi.", "Başarılı");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Modpack kurulum hatası: {ex.Message}", "Hata");
            }
            finally
            {
                if (tempMrpackPath != null) try { File.Delete(tempMrpackPath); } catch { }
            }
        }

        private void ExtractOverridesFolder(ZipArchive archive, string prefix, string destRoot)
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = entry.FullName.Substring(prefix.Length);
                if (string.IsNullOrEmpty(relative) || entry.FullName.EndsWith("/")) continue;

                string destPath = Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        // ==================== KURULU MODLAR SEKMESİ ====================

        private async void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainTabControl.SelectedIndex == 1) await LoadInstalledModsAsync();
        }

        private async void RefreshModsBtn_Click(object sender, RoutedEventArgs e)
        {
            await LoadInstalledModsAsync();
        }

        private async Task LoadInstalledModsAsync()
        {
            InstalledStatusText.Text = "Kurulu modlar taranıyor...";
            string modsFolder = Path.Combine(_gamePath.BasePath, "mods");
            string metaFolder = Path.Combine(_gamePath.BasePath, "modpacks_meta");

            var packMetas = new List<InstalledModpackMeta>();
            if (Directory.Exists(metaFolder))
            {
                foreach (var file in Directory.GetFiles(metaFolder, "*.json"))
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<InstalledModpackMeta>(File.ReadAllText(file));
                        if (meta != null) packMetas.Add(meta);
                    }
                    catch { }
                }
            }

            if (!Directory.Exists(modsFolder))
            {
                InstalledModsListBox.ItemsSource = null;
                InstalledStatusText.Text = "Henüz hiç mod indirilmemiş.";
                return;
            }

            var fileEntries = new List<(string FullPath, string CleanFileName, bool Enabled, string Hash)>();
            foreach (var filePath in Directory.GetFiles(modsFolder))
            {
                string fileName = Path.GetFileName(filePath);
                bool isEnabledJar = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                bool isDisabledJar = fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!isEnabledJar && !isDisabledJar) continue;

                string cleanName = isDisabledJar ? fileName.Substring(0, fileName.Length - ".disabled".Length) : fileName;
                string hash = string.Empty;
                try
                {
                    using var sha1 = System.Security.Cryptography.SHA1.Create();
                    using var stream = File.OpenRead(filePath);
                    hash = Convert.ToHexString(await Task.Run(() => sha1.ComputeHash(stream))).ToLowerInvariant();
                }
                catch { }
                fileEntries.Add((filePath, cleanName, isEnabledJar, hash));
            }

            var iconByHash = new Dictionary<string, (string Title, string? IconUrl)>();
            var validHashes = fileEntries.Where(f => !string.IsNullOrEmpty(f.Hash)).Select(f => f.Hash).Distinct().ToList();

            if (validHashes.Count > 0)
            {
                try
                {
                    string requestBody = JsonSerializer.Serialize(new { hashes = validHashes, algorithm = "sha1" });
                    var response = await _httpClient.PostAsync("https://api.modrinth.com/v2/version_files", new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"));

                    if (response.IsSuccessStatusCode)
                    {
                        var versionMap = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion>>(await response.Content.ReadAsStringAsync());
                        if (versionMap != null && versionMap.Count > 0)
                        {
                            var projectIds = versionMap.Values.Select(v => v.project_id).Distinct().ToList();
                            string projectsJson = await _httpClient.GetStringAsync($"https://api.modrinth.com/v2/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(projectIds))}");
                            var projectById = (JsonSerializer.Deserialize<List<ModrinthProject>>(projectsJson) ?? new()).ToDictionary(p => p.id, p => p);

                            foreach (var kvp in versionMap)
                            {
                                if (projectById.TryGetValue(kvp.Value.project_id, out var proj))
                                    iconByHash[kvp.Key] = (proj.title, proj.icon_url);
                            }
                        }
                    }
                }
                catch { }
            }

            var allMods = new List<InstalledMod>();
            foreach (var entry in fileEntries)
            {
                string displayName = entry.CleanFileName;
                string? iconUrl = null;
                if (!string.IsNullOrEmpty(entry.Hash) && iconByHash.TryGetValue(entry.Hash, out var info))
                {
                    displayName = info.Title;
                    iconUrl = info.IconUrl;
                }

                allMods.Add(new InstalledMod
                {
                    FullPath = entry.FullPath,
                    DisplayName = displayName,
                    IsEnabled = entry.Enabled,
                    IconUrl = iconUrl
                });
            }

            var displayItems = new List<IInstalledItem>();
            foreach (var meta in packMetas)
            {
                var group = new InstalledModpackGroup
                {
                    DisplayName = meta.Title,
                    IconUrl = meta.IconUrl
                };

                foreach (var jarName in meta.InstalledFiles)
                {
                    var mod = allMods.FirstOrDefault(m => Path.GetFileName(m.FullPath).Equals(jarName, StringComparison.OrdinalIgnoreCase) ||
                                                          Path.GetFileName(m.FullPath).Equals(jarName + ".disabled", StringComparison.OrdinalIgnoreCase));
                    if (mod != null)
                    {
                        group.Mods.Add(mod);
                        allMods.Remove(mod);
                    }
                }

                if (group.Mods.Count > 0)
                {
                    group.IsEnabled = group.Mods.Any(m => m.IsEnabled);
                    displayItems.Add(group);
                }
            }

            displayItems.AddRange(allMods);

            _lastLoadedItems = displayItems.OrderBy(x => x.DisplayName).ToList();
            InstalledModsListBox.ItemsSource = _lastLoadedItems;

            if (ModpackDetailView.Visibility == Visibility.Visible && _currentOpenGroup != null)
            {
                var updatedGroup = _lastLoadedItems.OfType<InstalledModpackGroup>().FirstOrDefault(g => g.DisplayName == _currentOpenGroup.DisplayName);
                if (updatedGroup != null)
                {
                    _currentOpenGroup = updatedGroup;
                    ModpackDetailListBox.ItemsSource = _currentOpenGroup.Mods;
                }
                else
                {
                    BackToMainInstalledView_Click(null!, null!);
                }
            }

            InstalledStatusText.Text = displayItems.Count == 0 ? "Henüz hiç mod indirilmemiş." : $"{displayItems.Count} içerik bulundu.";
        }

        private void InstalledModsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (InstalledModsListBox.SelectedItem is InstalledModpackGroup group)
            {
                _currentOpenGroup = group;
                InstalledMainView.Visibility = Visibility.Collapsed;
                ModpackDetailView.Visibility = Visibility.Visible;
                DetailPackTitle.Text = group.DisplayName;
                ModpackDetailListBox.ItemsSource = group.Mods;
            }
        }

        private async void BackToMainInstalledView_Click(object sender, RoutedEventArgs e)
        {
            _currentOpenGroup = null;
            ModpackDetailView.Visibility = Visibility.Collapsed;
            InstalledMainView.Visibility = Visibility.Visible;
            await LoadInstalledModsAsync();
        }

        private async void ModEnabledCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox || checkBox.Tag is not IInstalledItem item) return;

            try
            {
                bool targetState = checkBox.IsChecked ?? false;

                if (item is InstalledMod mod)
                {
                    ToggleModFile(mod, targetState);
                }
                else if (item is InstalledModpackGroup group)
                {
                    foreach (var childMod in group.Mods)
                    {
                        ToggleModFile(childMod, targetState);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Durum değiştirilemedi: {ex.Message}", "Hata");
            }
            finally
            {
                await LoadInstalledModsAsync();
            }
        }

        private void ToggleModFile(InstalledMod mod, bool targetState)
        {
            if (targetState && !mod.IsEnabled)
            {
                string enabledPath = mod.FullPath.Substring(0, mod.FullPath.Length - ".disabled".Length);
                File.Move(mod.FullPath, enabledPath);
                mod.FullPath = enabledPath;
                mod.IsEnabled = true;
            }
            else if (!targetState && mod.IsEnabled)
            {
                string disabledPath = mod.FullPath + ".disabled";
                File.Move(mod.FullPath, disabledPath);
                mod.FullPath = disabledPath;
                mod.IsEnabled = false;
            }
        }

        private async void DeleteModBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not IInstalledItem item) return;

            var confirm = MessageBox.Show($"'{item.DisplayName}' kalıcı olarak silinsin mi?", "Emin misin?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (item is InstalledMod mod)
                {
                    if (File.Exists(mod.FullPath))
                        File.Delete(mod.FullPath);
                }
                else if (item is InstalledModpackGroup group)
                {
                    foreach (var childMod in group.Mods)
                    {
                        if (File.Exists(childMod.FullPath))
                            File.Delete(childMod.FullPath);
                    }

                    string safeTitle = string.Join("_", group.DisplayName.Split(Path.GetInvalidFileNameChars()));
                    string metaPath = Path.Combine(_gamePath.BasePath, "modpacks_meta", $"{safeTitle}.json");
                    if (File.Exists(metaPath))
                        File.Delete(metaPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Silinemedi: {ex.Message}", "Hata");
            }
            finally
            {
                await LoadInstalledModsAsync();
            }
        }

        private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession == null)
            {
                MessageBox.Show("Lütfen önce giriş yapın!", "Hata");
                return;
            }

            DownloadBtn.IsEnabled = false;
            StatusText.Text = "Fabric loader kontrol ediliyor...";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += (senderCer, cert, chain, sslPolicyErrors) => true;

            try
            {
                var watcherObject = new MinecraftLauncher(_gamePath);
                watcherObject.FileProgressChanged += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"İniyor: {args.Name}";
                        DownloadProgressBar.Maximum = args.TotalTasks;
                        DownloadProgressBar.Value = args.ProgressedTasks;
                    });
                };

                // FabricInstaller de artık paylaşılan HttpClient'ı kullanıyor.
                var fabricInstaller = new FabricInstaller(_httpClient);
                string fabricVersionName = !string.IsNullOrEmpty(_preferredFabricLoaderVersion)
                    ? await fabricInstaller.Install(GameVersion, _preferredFabricLoaderVersion, _gamePath)
                    : await fabricInstaller.Install(GameVersion, _gamePath);

                StatusText.Text = "Oyun dosyaları indiriliyor...";

                var process = await Task.Run(async () =>
                {
                    return await watcherObject.CreateProcessAsync(fabricVersionName, new MLaunchOption
                    {
                        MaximumRamMb = 2048,
                        Session = _currentSession
                    });
                });

                StatusText.Text = "Oyun Başlatılıyor!";
                process.Start();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bir hata oluştu: {ex.Message}", "Hata!");
                DownloadBtn.IsEnabled = true;
                StatusText.Text = "Hata oluştu, tekrar dene.";
            }
        }
    }

    // ---- Modeller ----
    public interface IInstalledItem
    {
        string DisplayName { get; }
        string? IconUrl { get; }
        bool IsPack { get; }
        bool IsEnabled { get; set; }
        string SubText { get; }
    }

    public class InstalledMod : IInstalledItem
    {
        public string FullPath { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string? IconUrl { get; set; }
        public bool IsPack => false;
        public string SubText => string.Empty;
    }

    public class InstalledModpackGroup : IInstalledItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public string? IconUrl { get; set; }
        public List<InstalledMod> Mods { get; set; } = new List<InstalledMod>();
        public bool IsPack => true;
        public bool IsEnabled { get; set; } = true;
        public string SubText => $"{Mods.Count} Mod";
    }

    public class InstalledModpackMeta
    {
        public string Title { get; set; } = string.Empty;
        public string? IconUrl { get; set; }
        public List<string> InstalledFiles { get; set; } = new List<string>();
    }

    public class ModrinthSearchResponse { public List<ModrinthHit> hits { get; set; } = new(); }
    public class ModrinthHit { public string title { get; set; } = ""; public string description { get; set; } = ""; public string project_id { get; set; } = ""; public string slug { get; set; } = ""; public int downloads { get; set; } public string? icon_url { get; set; } public string project_type { get; set; } = "mod"; }
    public class ModrinthVersion { public string id { get; set; } = ""; public string project_id { get; set; } = ""; public string version_number { get; set; } = ""; public List<ModrinthFile> files { get; set; } = new(); public List<ModrinthDependency> dependencies { get; set; } = new(); }
    public class ModrinthFile { public string url { get; set; } = ""; public string filename { get; set; } = ""; public bool primary { get; set; } }
    public class ModrinthDependency { public string? version_id { get; set; } public string? project_id { get; set; } public string? file_name { get; set; } public string dependency_type { get; set; } = ""; }
    public class MrpackManifest { public int formatVersion { get; set; } public string game { get; set; } = ""; public string versionId { get; set; } = ""; public string name { get; set; } = ""; public string? summary { get; set; } public List<MrpackFile> files { get; set; } = new(); public Dictionary<string, string> dependencies { get; set; } = new(); }
    public class MrpackFile { public string path { get; set; } = ""; public List<string> downloads { get; set; } = new(); public long fileSize { get; set; } public MrpackEnv? env { get; set; } }
    public class MrpackEnv { public string? client { get; set; } public string? server { get; set; } }
    public class ModrinthProject { public string id { get; set; } = ""; public string title { get; set; } = ""; public string? icon_url { get; set; } }
}