using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Loader
{
    /// <summary>Простой вопрос-диалог (без Microsoft.VisualBasic).</summary>
    public static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string message, string def = "")
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(420, 140);

                var lbl = new Label { Left = 12, Top = 12, Width = 396, Text = message };
                var tb = new TextBox { Left = 12, Top = 44, Width = 396, Text = def };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 96, Width = 75 };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 333, Top = 96, Width = 75 };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }

    public class MainForm : Form
    {
        private readonly string _baseDir;
        private readonly string _manifestPath;
        private readonly string _statePath;
        private readonly string _downloadsDir;

        private ListView _lvPacks;
        private TextBox _txtManifestUrl;
        private ProgressBar _progress;
        private Label _lblStatus;
        private TextBox _txtLog;
        private Button _btnReloadJson;
        private Button _btnDownload;
        private Button _btnRun;
        private Button _btnOpenFolder;
        private ToolTip _tip = new ToolTip();

        private LauncherManifest _manifest = new LauncherManifest();
        private LoaderState _state = new LoaderState();
        private Thread _worker;
        private volatile bool _busy;

        // имя профиля fabric, которое поставили для выбранного пака (для кнопки «Играть»)
        private string _installedProfileId;

        public MainForm(string[] args)
        {
            Text = "Loader — загрузчик сборок (без авторизации)";
            Width = 860;
            Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(720, 520);

            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _manifestPath = Path.Combine(_baseDir, "launcher.json");
            _statePath = Path.Combine(_baseDir, "state.json");
            _downloadsDir = Path.Combine(_baseDir, "downloads");

            Downloader.InitTls();
            LauncherConfig.SaveDefaultIfMissing(_manifestPath);
            _state = LauncherConfig.LoadState(_statePath);

            BuildUi();

            if (args != null && args.Length > 0 &&
                (args[0].StartsWith("http://") || args[0].StartsWith("https://")))
            {
                _txtManifestUrl.Text = args[0];
                BeginReloadFromUrl(args[0]);
            }
            else
            {
                ReloadLocalManifest();
            }
        }

        // ============================ UI ============================

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(8) };
            var lblUrl = new Label { Text = "URL .json:", Left = 8, Top = 12, Width = 70, AutoSize = true };
            _txtManifestUrl = new TextBox
            {
                Left = 82,
                Top = 9,
                Width = 560,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = _state.ManifestUrl ?? ""
            };
            _btnReloadJson = new Button
            {
                Text = "Загрузить JSON",
                Left = 650,
                Top = 4,
                Width = 130,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnReloadJson.Click += (s, e) => OnReloadJsonClick();

            var lblHint = new Label
            {
                Text = "Или локальный launcher.json рядом с Loader.exe (создаётся автоматически).",
                Left = 82, Top = 36, AutoSize = true, ForeColor = Color.Gray
            };

            top.Controls.AddRange(new Control[] { lblUrl, _txtManifestUrl, _btnReloadJson, lblHint });

            _lvPacks = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true
            };
            _lvPacks.Columns.Add("Название", 260);
            _lvPacks.Columns.Add("Версия MC", 100);
            _lvPacks.Columns.Add("Fabric", 80);
            _lvPacks.Columns.Add("Тип", 70);
            _lvPacks.Columns.Add("Статус", 140);
            _lvPacks.Columns.Add("URL", 400);
            _lvPacks.DoubleClick += (s, e) => OnDownloadClick();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 210 };

            var btnPanel = new Panel { Dock = DockStyle.Top, Height = 46 };
            _btnDownload = MakeButton("⬇ Скачать и установить", 180, 0);
            _btnDownload.Click += (s, e) => OnDownloadClick();
            _btnRun = MakeButton("▶ Играть", 110, 190);
            _btnRun.Click += (s, e) => OnPlayClick();
            _btnRun.Enabled = false;
            _btnOpenFolder = MakeButton("📂 Папка сборки", 140, 310);
            _btnOpenFolder.Click += (s, e) => OpenPackFolder();
            var btnVanilla = MakeButton("Скачать vanilla-файлы", 180, 460);
            btnVanilla.Click += (s, e) => OnVanillaClick();
            btnPanel.Controls.AddRange(new Control[] { _btnDownload, _btnRun, _btnOpenFolder, btnVanilla });

            _lblStatus = new Label { Dock = DockStyle.Top, Height = 22, Text = "Готов.", Padding = new Padding(4, 4, 0, 0) };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 100 };

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 9f)
            };

            bottom.Controls.Add(_txtLog);
            bottom.Controls.Add(_progress);
            bottom.Controls.Add(_lblStatus);
            bottom.Controls.Add(btnPanel);

            Controls.Add(_lvPacks);
            Controls.Add(bottom);
            Controls.Add(top);

            _tip.SetToolTip(_btnDownload, "Скачает zip/exe из JSON, распакует/запустит и поставит Fabric под версию.");
            _tip.SetToolTip(_btnRun, "Запуск через java (нужен установленный Java 17+/8 для старых версий).");
        }

        private Button MakeButton(string text, int width, int left)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Left = left,
                Top = 8,
                Height = 30,
                FlatStyle = FlatStyle.System
            };
        }

        // ============================ MANIFEST ============================

        private void OnReloadJsonClick()
        {
            string url = (_txtManifestUrl.Text ?? "").Trim();
            if (string.IsNullOrEmpty(url))
            {
                ReloadLocalManifest();
                return;
            }
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                BeginReloadFromUrl(url);
            }
            else if (File.Exists(url))
            {
                LoadManifestFromFile(url);
            }
            else
            {
                Log("! Файл не найден: " + url);
            }
        }

        private void BeginReloadFromUrl(string url)
        {
            RunAsync("Загрузка манифеста...", () =>
            {
                try
                {
                    string json = Downloader.ReadAllText(url, default(CancellationToken));
                    string tmp = Path.Combine(_downloadsDir, "launcher.remote.json");
                    Directory.CreateDirectory(_downloadsDir);
                    File.WriteAllText(tmp, json);
                    Post(() => LoadManifestFromText(json, "удалённый манифест"));
                    _state.ManifestUrl = url;
                    LauncherConfig.SaveState(_statePath, _state);
                }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        Log("! Не удалось скачать JSON: " + ex.Message);
                        Log("  Использую локальный launcher.json.");
                        ReloadLocalManifest();
                    });
                }
            });
        }

        private void ReloadLocalManifest()
        {
            try { LoadManifestFromFile(_manifestPath); }
            catch (Exception ex) { Log("! Ошибка чтения launcher.json: " + ex.Message); }
        }

        private void LoadManifestFromFile(string path)
        {
            LoadManifestFromText(File.ReadAllText(path), Path.GetFileName(path));
        }

        private void LoadManifestFromText(string text, string sourceName)
        {
            try
            {
                var m = LauncherConfig.Load(_manifestPath);
                // парсим переданный текст напрямую
                var root = Json.Obj(Json.Parse(text));
                m = new LauncherManifest();
                if (root != null)
                {
                    m.MinecraftDir = Json.Str(root, "minecraftDir", "");
                    m.JavaPath = Json.Str(root, "javaPath", "");
                    foreach (var o in Json.ArrOf(root, "jvmArgs"))
                        if (o is string) m.JvmArgs.Add((string)o);
                    foreach (var po in Json.ArrOf(root, "packs"))
                    {
                        var pd = Json.Obj(po);
                        if (pd == null) continue;
                        m.Packs.Add(new PackInfo
                        {
                            Name = Json.Str(pd, "name", "Без имени"),
                            Url = Json.Str(pd, "url"),
                            Version = Json.Str(pd, "version"),
                            UseFabric = Json.Bool(pd, "useFabric", true)
                        });
                    }
                }
                _manifest = m;
                FillList();
                Log("* Манифест загружен: " + sourceName + " (" + m.Packs.Count + " сборок).");
            }
            catch (Exception ex)
            {
                Log("! Битый JSON: " + ex.Message);
            }
        }

        private void FillList()
        {
            _lvPacks.BeginUpdate();
            _lvPacks.Items.Clear();
            foreach (var p in _manifest.Packs)
            {
                string type = GuessType(p.Url);
                var item = new ListViewItem(p.Name);
                item.SubItems.Add(p.Version);
                item.SubItems.Add(p.UseFabric ? "да" : "нет");
                item.SubItems.Add(type);
                item.SubItems.Add(PackStatus(p));
                item.SubItems.Add(p.Url);
                item.Tag = p;
                _lvPacks.Items.Add(item);
            }
            _lvPacks.EndUpdate();

            // восстановить выбор
            if (!string.IsNullOrEmpty(_state.SelectedPack))
            {
                foreach (ListViewItem it in _lvPacks.Items)
                {
                    var pp = it.Tag as PackInfo;
                    if (pp != null && pp.Name == _state.SelectedPack)
                    {
                        it.Selected = true;
                        it.Focused = true;
                        break;
                    }
                }
            }
            UpdateRunButton();
        }

        private static string GuessType(string url)
        {
            try
            {
                string name = Path.GetFileName(new Uri(url).AbsolutePath).ToLowerInvariant();
                if (name.EndsWith(".exe")) return "EXE";
                if (name.EndsWith(".zip")) return "ZIP";
            }
            catch { }
            return "?";
        }

        private string PackStatus(PackInfo p)
        {
            string dir = PackDir(p);
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
                return "скачано ✔";
            return "не скачано";
        }

        private PackInfo Selected()
        {
            if (_lvPacks.SelectedItems.Count == 0) return null;
            return _lvPacks.SelectedItems[0].Tag as PackInfo;
        }

        private string PackDir(PackInfo p)
        {
            string safe = string.Join("_", p.Name.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_downloadsDir, safe);
        }

        private string MinecraftDir()
        {
            string d = _manifest.MinecraftDir;
            if (string.IsNullOrWhiteSpace(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            else
                d = Environment.ExpandEnvironmentVariables(d);
            Directory.CreateDirectory(d);
            return d;
        }

        // ============================ ДЕЙСТВИЯ ============================

        private void OnDownloadClick()
        {
            var pack = Selected();
            if (pack == null) { MessageBox.Show(this, "Выбери сборку в списке.", "Loader"); return; }
            _state.SelectedPack = pack.Name;
            LauncherConfig.SaveState(_statePath, _state);
            if (_busy) return;

            RunAsync("Скачивание: " + pack.Name, () =>
            {
                try
                {
                    InstallPack(pack);
                    Post(() =>
                    {
                        SetStatus("Готово: " + pack.Name);
                        FillList();
                        _installedProfileId = FindProfileForVersion(pack.Version);
                        UpdateRunButton();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        SetStatus("Ошибка: " + ex.Message);
                        Log("! " + ex.Message);
                    });
                }
            });
        }

        private void InstallPack(PackInfo pack)
        {
            Log("");
            Log("=== " + pack.Name + " (MC " + pack.Version + ") ===");

            string dir = PackDir(pack);
            Directory.CreateDirectory(dir);

            string fileName = Downloader.GetFileNameFromUrl(pack.Url);
            string dest = Path.Combine(dir, fileName);
            string lower = fileName.ToLowerInvariant();

            Log("[1/3] Скачиваю " + pack.Url);
            Downloader.DownloadFile(pack.Url, dest, (done, total) =>
            {
                if (total > 0)
                    Post(() => SetProgress((int)(done * 100 / total), done + " / " + total + " байт"));
                else
                    Post(() => SetProgress(-1, done + " байт..."));
            }, default(CancellationToken));
            Post(() => SetProgress(100, "Файл скачан."));

            // EXE — просто положить и запустить
            if (lower.EndsWith(".exe"))
            {
                Log("[2/3] Это .exe — запускаю установщик из папки: " + dir);
                try
                {
                    var pr = Downloader.StartExe(dest, dir);
                    Log("     Процесс запущен, PID=" + pr.Id);
                }
                catch (Exception ex)
                {
                    Log("     ! Не удалось запустить: " + ex.Message);
                }
                Log("[3/3] Готово (exe).");
                return;
            }

            // ZIP — распаковываем. Моды jar -> mods/, остальное по структуре архива.
            if (lower.EndsWith(".zip"))
            {
                Log("[2/3] Распаковываю ZIP...");
                string extractRoot = Path.Combine(dir, "extracted");
                Downloader.ExtractZip(dest, extractRoot);
                FlattenAndSort(dir, extractRoot);
                Log("     Распаковано в " + dir);
            }
            else
            {
                Log("[2/3] Неизвестный тип файла — оставляю как есть.");
            }

            // FABRIC под версию из json
            if (pack.UseFabric && !string.IsNullOrWhiteSpace(pack.Version))
            {
                Log("[3/3] Устанавливаю Fabric для версии " + pack.Version + " ...");
                string mcDir = MinecraftDir();
                string profileId = FabricApi.Install(mcDir, pack.Version, s => Post(() => Log(s)));
                _installedProfileId = profileId;
                Log("     Fabric установлен. Профиль: " + profileId);

                // складываем jar-моды из сборки в mods этой папки
                CopyModsToMinecraft(dir, mcDir);
            }
            else
            {
                Log("[3/3] Fabric не требуется.");
            }

            Log("=== Установка завершена ===");
        }

        /// <summary>Поднимает содержимое из extracted/* на уровень папки сборки.</summary>
        private void FlattenAndSort(string packDir, string extractRoot)
        {
            try
            {
                string src = extractRoot;
                // если внутри одна корневая папка — спускаемся в неё
                var dirs = Directory.GetDirectories(src);
                var files = Directory.GetFiles(src);
                if (dirs.Length == 1 && files.Length == 0) src = dirs[0];

                foreach (var f in Directory.GetFiles(src))
                    MoveTo(f, Path.Combine(packDir, Path.GetFileName(f)));
                foreach (var d in Directory.GetDirectories(src))
                    MoveTo(d, Path.Combine(packDir, Path.GetFileName(d)));

                if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);

                // все .jar с корня сборки переносим в mods/
                string modsDir = Path.Combine(packDir, "mods");
                foreach (var jar in Directory.GetFiles(packDir, "*.jar"))
                {
                    Directory.CreateDirectory(modsDir);
                    MoveTo(jar, Path.Combine(modsDir, Path.GetFileName(jar)));
                }
            }
            catch (Exception ex)
            {
                Log("     ! сортировка: " + ex.Message);
            }
        }

        private static void MoveTo(string from, string to)
        {
            if (File.Exists(to) || Directory.Exists(to))
            {
                try { if (File.Exists(to)) File.Delete(to); else Directory.Delete(to, true); } catch { }
            }
            if (File.Exists(from)) File.Move(from, to);
            else if (Directory.Exists(from)) Directory.Move(from, to);
        }

        private void CopyModsToMinecraft(string packDir, string mcDir)
        {
            try
            {
                string srcMods = Path.Combine(packDir, "mods");
                if (!Directory.Exists(srcMods)) return;
                string dstMods = Path.Combine(mcDir, "mods");
                Directory.CreateDirectory(dstMods);
                int n = 0;
                foreach (var jar in Directory.GetFiles(srcMods, "*.jar"))
                {
                    string target = Path.Combine(dstMods, Path.GetFileName(jar));
                    File.Copy(jar, target, true);
                    n++;
                }
                Log("     В .minecraft/mods скопировано модов: " + n);
            }
            catch (Exception ex)
            {
                Log("     ! копирование модов: " + ex.Message);
            }
        }

        private void OnVanillaClick()
        {
            var pack = Selected();
            string version = pack != null && !string.IsNullOrWhiteSpace(pack.Version)
                ? pack.Version
                : Prompt.Show(this, "Loader", "Для какой версии скачать vanilla-файлы?", "1.21.4");
            if (string.IsNullOrWhiteSpace(version)) return;

            RunAsync("Скачивание vanilla " + version, () =>
            {
                try
                {
                    VanillaApi.EnsureVanilla(MinecraftDir(), version.Trim(), s => Post(() => Log(s)));
                    Post(() => SetStatus("Vanilla " + version + " готова."));
                }
                catch (Exception ex)
                {
                    Post(() => Log("! vanilla: " + ex.Message));
                }
            });
        }

        private void OnPlayClick()
        {
            var pack = Selected();
            if (pack == null) return;
            string profileId = _installedProfileId ?? FindProfileForVersion(pack.Version);
            if (string.IsNullOrEmpty(profileId))
            {
                MessageBox.Show(this,
                    "Fabric-профиль для версии " + pack.Version + " ещё не установлен.\nНажми «Скачать и установить».",
                    "Loader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RunAsync("Запуск " + profileId, () =>
            {
                try
                {
                    LaunchGame(profileId, pack.Version);
                }
                catch (Exception ex)
                {
                    Post(() => Log("! Запуск: " + ex.Message));
                }
            });
        }

        private string FindProfileForVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            try { return FabricApi.FindInstalledFabricProfile(MinecraftDir(), version); }
            catch { return null; }
        }

        private void LaunchGame(string profileId, string gameVersion)
        {
            string mcDir = MinecraftDir();
            string profilePath = Path.Combine(mcDir, "versions", profileId, profileId + ".json");
            if (!File.Exists(profilePath)) throw new FileNotFoundException("Профиль не найден: " + profilePath);

            var prof = Json.Obj(Json.Parse(File.ReadAllText(profilePath)));
            string mainClass = Json.Str(prof, "mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient");

            string java = FabricApi.FindJava(_manifest.JavaPath);
            if (java == null)
                throw new Exception("Java не найдена. Установи Java 17+ (для 1.21.x) или Java 8 (для 1.16.5), " +
                                    "либо пропиши путь в launcher.json -> \"javaPath\".");

            // Разворачиваем библиотеки профиля (maven -> пути)
            var cpFiles = new List<string>();
            foreach (var lo in Json.ArrOf(prof, "libraries"))
            {
                var ld = Json.Obj(lo);
                if (ld == null) continue;
                string name = Json.Str(ld, "name");
                if (string.IsNullOrEmpty(name)) continue;
                string rel = MavenToPath(name);
                string libFile = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(libFile)) cpFiles.Add(libFile);
            }

            // client jar версии (наследуется от vanilla)
            string inherits = Json.Str(prof, "inheritsFrom", gameVersion);
            string clientJar = Path.Combine(mcDir, "versions", inherits, inherits + ".jar");
            if (!File.Exists(clientJar))
                throw new Exception("Не найден client.jar версии " + inherits +
                                    ". Нажми «Скачать vanilla-файлы» и повтори.");
            cpFiles.Add(clientJar);

            string classpath = "\"" + string.Join(";" , cpFiles) + "\"";

            // аргументы
            var tokens = new Dictionary<string, string>
            {
                { "${auth_player_name}", "Player_" + new Random().Next(1000, 9999) }, // оффлайн-ник без авторизации
                { "${version_name}", profileId },
                { "${game_directory}", mcDir },
                { "${assets_root}", Path.Combine(mcDir, "assets") },
                { "${game_assets}", Path.Combine(mcDir, "assets", "virtual", "legacy") },
                { "${assets_index_name}", GetAssetIndex(mcDir, inherits) },
                { "${auth_uuid}", OfflineUuid("Player") },
                { "${auth_access_token}", "0" },
                { "${auth_xuid}", "0" },
                { "${clientid}", Guid.NewGuid().ToString() },
                { "${user_type}", "legacy" },
                { "${version_type}", "Fabric" },
                { "${natives_directory}", Path.Combine(mcDir, "versions", profileId, "natives") },
                { "${launcher_name}", "Loader" },
                { "${launcher_version}", "1.0" },
                { "${classpath}", classpath }
            };

            var psi = new ProcessStartInfo(java) { UseShellExecute = false };
            string argLine = "";
            foreach (var a in _manifest.JvmArgs) argLine += Quoted(a) + " ";
            argLine += "-Djava.library.path=" + Quoted(tokens["${natives_directory}"]) + " ";
            argLine += "-cp " + classpath + " " + mainClass; // classpath уже в кавычках
            foreach (var t in BuildGameArgs(prof, inherits))
                argLine += " " + Quoted(Substitute(t, tokens));
            psi.Arguments = argLine;

            Log("  запуск: " + java);
            var p = Process.Start(psi);
            Log("  Minecraft запущен, PID=" + p.Id);
            Post(() => SetStatus("Игра запущена (PID " + p.Id + ")."));
        }

        private static IEnumerable<string> BuildGameArgs(Dictionary<string, object> prof, string inheritsMcDirVersion)
        {
            // сначала собственные arguments, иначе minecraftArguments, иначе дефолт
            var argsObj = Json.Obj(prof.ContainsKey("arguments") ? prof["arguments"] : null);
            if (argsObj != null)
            {
                foreach (var g in Json.ArrOf(argsObj, "game"))
                    if (g is string) yield return (string)g;
                yield break;
            }
            string legacy = Json.Str(prof, "minecraftArguments", "");
            if (!string.IsNullOrEmpty(legacy))
            {
                foreach (var t in legacy.Split(' '))
                    if (!string.IsNullOrWhiteSpace(t)) yield return t;
                yield break;
            }
            foreach (var t in new[]
            {
                "--username", "${auth_player_name}", "--version", "${version_name}",
                "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}",
                "--assetIndex", "${assets_index_name}", "--uuid", "${auth_uuid}",
                "--accessToken", "${auth_access_token}", "--userType", "${user_type}",
                "--versionType", "${version_type}"
            })
                yield return t;
        }

        private static string Substitute(string token, Dictionary<string, string> map)
        {
            string v;
            return map.TryGetValue(token, out v) ? v : token;
        }

        private static string Quoted(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool need = s.IndexOf(' ') >= 0 || s.IndexOf(';') >= 0 || s.IndexOf('"') >= 0;
            s = s.Replace("\"", "\\\"");
            return need ? "\"" + s + "\"" : s;
        }

        private static bool IsWindowsNative(string mavenName)
        {
            return mavenName.IndexOf(":natives-windows", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string MavenToPath(string name)
        {
            string[] parts = name.Split(':');
            if (parts.Length < 3) return name;
            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            string classifier = parts.Length >= 4 ? "-" + parts[3] : "";
            string ext = ".jar";
            if (parts.Length == 4 && parts[3].StartsWith("@")) { ext = "." + parts[3].Substring(1); classifier = ""; }
            return group + "/" + artifact + "/" + version + "/" + artifact + "-" + version + classifier + ext;
        }

        private string GetAssetIndex(string mcDir, string version)
        {
            try
            {
                string vp = Path.Combine(mcDir, "versions", version, version + ".json");
                var vj = Json.Obj(Json.Parse(File.ReadAllText(vp)));
                var ai = Json.Obj(vj != null && vj.ContainsKey("assetIndex") ? vj["assetIndex"] : null);
                string id = ai != null ? Json.Str(ai, "id") : version;
                return string.IsNullOrEmpty(id) ? version : id;
            }
            catch { return version; }
        }

        private static string OfflineUuid(string name)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
                hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
                hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
                return new Guid(hash).ToString("N");
            }
        }

        private void OpenPackFolder()
        {
            var pack = Selected();
            if (pack == null) return;
            string dir = PackDir(pack);
            Directory.CreateDirectory(dir);
            try { Process.Start("explorer.exe", dir); } catch { }
        }

        // ============================ HELPERS ============================

        private void RunAsync(string status, Action work)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(status);
            _btnDownload.Enabled = false;
            _btnRun.Enabled = false;
            _btnReloadJson.Enabled = false;
            _worker = new Thread(() =>
            {
                try { work(); }
                finally
                {
                    Post(() =>
                    {
                        _busy = false;
                        _btnDownload.Enabled = true;
                        _btnReloadJson.Enabled = true;
                        UpdateRunButton();
                        SetProgress(0, "");
                    });
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void UpdateRunButton()
        {
            var pack = Selected();
            _btnRun.Enabled = !_busy && pack != null &&
                              !string.IsNullOrEmpty(FindProfileForVersion(pack.Version) ?? _installedProfileId);
        }

        private void Post(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }

        private void SetStatus(string s) { if (!IsDisposed) _lblStatus.Text = s; }

        private void SetProgress(int percent, string text)
        {
            if (IsDisposed) return;
            _progress.Value = Math.Max(0, Math.Min(100, percent < 0 ? 0 : percent));
            if (!string.IsNullOrEmpty(text)) _lblStatus.Text = text;
        }

        private void Log(string line)
        {
            if (IsDisposed) return;
            if (_txtLog.InvokeRequired) { Post(() => Log(line)); return; }
            _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }
    }
}
