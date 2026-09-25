using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Loader
{
    /// <summary>
    /// Ядро лоадера без GUI (используется и CLI, и формой).
    /// Логика: берём launcher.json (локальный или с GitHub) -> для каждой сборки:
    ///   .exe  — скачать, положить в папку, запустить;
    ///   .zip  — скачать, распаковать, jar-моды сложить в mods/;
    ///   версия из json (1.21.4 / 1.21.11 / 1.16.5 ...) — скачать Fabric именно под неё.
    /// Запуск игры — оффлайн (без авторизации).
    /// </summary>
    public class Engine
    {
        public string BaseDir;
        public string DownloadsDir;
        public LauncherManifest Manifest = new LauncherManifest();
        public string SourceName = "";

        private readonly Action<string> _log;

        public Engine(string baseDir, Action<string> log)
        {
            BaseDir = baseDir;
            DownloadsDir = Path.Combine(baseDir, "downloads");
            _log = log ?? delegate { };
        }

        // ============================ МАНИФЕСТ ============================

        public static bool IsUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>GitHub web-ссылку на файл превращает в raw.githubusercontent.com.</summary>
        public static string NormalizeGithubUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = url.Trim();
            try
            {
                var u = new Uri(url);
                string host = u.Host.ToLowerInvariant();
                if (host == "github.com")
                {
                    string p = u.AbsolutePath.Trim('/');
                    int idx = p.IndexOf("/blob/", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                        return "https://raw.githubusercontent.com/" +
                               p.Substring(0, idx) + "/" + p.Substring(idx + 6);
                }
                else if (host == "gist.github.com")
                {
                    if (!url.EndsWith("/raw", StringComparison.OrdinalIgnoreCase))
                        url += "/raw";
                }
            }
            catch { }
            return url;
        }

        /// <summary>Загружает манифест: по URL (GitHub) или из локального файла.</summary>
        public void LoadManifest(string source)
        {
            string text;
            source = (source ?? "").Trim();

            if (string.IsNullOrEmpty(source))
            {
                string local = Path.Combine(BaseDir, "launcher.json");
                if (!File.Exists(local))
                {
                    LauncherConfig.SaveDefaultIfMissing(local);
                    _log("* создан шаблон launcher.json: " + local);
                }
                text = File.ReadAllText(local);
                SourceName = "launcher.json (локальный)";
            }
            else if (IsUrl(source))
            {
                string url = NormalizeGithubUrl(source);
                _log("* качаю манифест: " + url);
                text = Downloader.ReadAllText(url, default(System.Threading.CancellationToken));
                Directory.CreateDirectory(DownloadsDir);
                File.WriteAllText(Path.Combine(DownloadsDir, "launcher.remote.json"), text);
                SourceName = url;
            }
            else if (File.Exists(source))
            {
                text = File.ReadAllText(source);
                SourceName = Path.GetFullPath(source);
            }
            else throw new FileNotFoundException("Манифест не найден: " + source);

            Manifest = ParseManifest(text);
            _log("* манифест загружен: " + SourceName + " (" + Manifest.Packs.Count + " сборок)");
        }

        public static LauncherManifest ParseManifest(string text)
        {
            var m = new LauncherManifest();
            var root = Json.Obj(Json.Parse(text));
            if (root == null) return m;

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
            return m;
        }

        // ============================ ПУТИ ============================

        public string PackDir(PackInfo p)
        {
            string safe = "";
            foreach (char c in (p.Name ?? "pack"))
                safe += Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? "_" : c.ToString();
            return Path.Combine(DownloadsDir, safe);
        }

        public string MinecraftDir()
        {
            string d = Manifest.MinecraftDir;
            if (string.IsNullOrWhiteSpace(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            else d = Environment.ExpandEnvironmentVariables(d);
            Directory.CreateDirectory(d);
            return d;
        }

        public static string GuessType(string url)
        {
            try
            {
                string name = Path.GetFileName(new Uri(url).AbsolutePath).ToLowerInvariant();
                if (name.EndsWith(".exe")) return "EXE";
                if (name.EndsWith(".zip")) return "ZIP";
            }
            catch { }
            return "BIN";
        }

        public string PackStatus(PackInfo p)
        {
            string dir = PackDir(p);
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
                return "скачано";
            return "нет";
        }

        // ============================ УСТАНОВКА ============================

        /// <summary>Скачать сборку (zip/exe), распаковать, поставить Fabric под версию из json.</summary>
        public void InstallPack(PackInfo pack)
        {
            _log("");
            _log("=== " + pack.Name + " (MC " + pack.Version + ") ===");

            string dir = PackDir(pack);
            Directory.CreateDirectory(dir);

            string fileName = Downloader.GetFileNameFromUrl(pack.Url);
            string dest = Path.Combine(dir, fileName);
            string lower = fileName.ToLowerInvariant();

            _log("[1/3] скачиваю " + pack.Url);
            long lastPct = -1;
            Downloader.DownloadFile(pack.Url, dest, delegate(long done, long total)
            {
                if (total <= 0) return;
                long pct = done * 100 / total;
                if (pct >= lastPct + 5 || done == total)
                {
                    lastPct = pct;
                    _log("      " + pct + "% (" + (done / 1024) + " / " + (total / 1024) + " КБ)");
                }
            }, default(System.Threading.CancellationToken));

            if (lower.EndsWith(".exe"))
            {
                _log("[2/3] это .exe — запускаю из папки " + dir);
                try
                {
                    var pr = Downloader.StartExe(dest, dir);
                    _log("      процесс запущен, PID=" + pr.Id);
                }
                catch (Exception ex) { _log("      ! не удалось запустить: " + ex.Message); }
                _log("[3/3] готово (exe).");
                return;
            }

            if (lower.EndsWith(".zip"))
            {
                _log("[2/3] распаковываю zip...");
                string extractRoot = Path.Combine(dir, "extracted");
                Downloader.ExtractZip(dest, extractRoot);
                FlattenAndSort(dir, extractRoot);
                _log("      распаковано в " + dir);
            }
            else _log("[2/3] неизвестный тип — оставил файл как есть.");

            if (pack.UseFabric && !string.IsNullOrWhiteSpace(pack.Version))
            {
                _log("[3/3] ставлю Fabric для " + pack.Version + " ...");
                string mcDir = MinecraftDir();
                string profileId = FabricApi.Install(mcDir, pack.Version, _log);
                _log("      Fabric установлен, профиль: " + profileId);
                CopyModsToMinecraft(dir, mcDir);
            }
            else _log("[3/3] Fabric не требуется.");

            _log("=== установка завершена: " + pack.Name + " ===");
        }

        /// <summary>Поднимает содержимое extracted/* на уровень папки сборки, jar-ы складывает в mods/.</summary>
        public void FlattenAndSort(string packDir, string extractRoot)
        {
            try
            {
                string src = extractRoot;
                var dirs = Directory.GetDirectories(src);
                var files = Directory.GetFiles(src);
                if (dirs.Length == 1 && files.Length == 0) src = dirs[0];

                foreach (var f in Directory.GetFiles(src))
                    MoveTo(f, Path.Combine(packDir, Path.GetFileName(f)));
                foreach (var d in Directory.GetDirectories(src))
                    MoveTo(d, Path.Combine(packDir, Path.GetFileName(d)));

                if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);

                string modsDir = Path.Combine(packDir, "mods");
                foreach (var jar in Directory.GetFiles(packDir, "*.jar"))
                {
                    Directory.CreateDirectory(modsDir);
                    MoveTo(jar, Path.Combine(modsDir, Path.GetFileName(jar)));
                }
            }
            catch (Exception ex) { _log("      ! сортировка: " + ex.Message); }
        }

        public static void MoveTo(string from, string to)
        {
            if (File.Exists(to) || Directory.Exists(to))
            {
                try { if (File.Exists(to)) File.Delete(to); else Directory.Delete(to, true); } catch { }
            }
            if (File.Exists(from)) File.Move(from, to);
            else if (Directory.Exists(from)) Directory.Move(from, to);
        }

        public void CopyModsToMinecraft(string packDir, string mcDir)
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
                    File.Copy(jar, Path.Combine(dstMods, Path.GetFileName(jar)), true);
                    n++;
                }
                _log("      в .minecraft/mods скопировано модов: " + n);
            }
            catch (Exception ex) { _log("      ! копирование модов: " + ex.Message); }
        }

        // ============================ ЗАПУСК (оффлайн) ============================

        public void LaunchGame(PackInfo pack)
        {
            string mcDir = MinecraftDir();
            string profileId = FabricApi.FindInstalledFabricProfile(mcDir, pack.Version);
            if (string.IsNullOrEmpty(profileId))
                throw new Exception("Fabric-профиль для " + pack.Version + " не установлен — сначала «install».");

            string profilePath = Path.Combine(mcDir, "versions", profileId, profileId + ".json");
            if (!File.Exists(profilePath))
                throw new FileNotFoundException("Профиль не найден: " + profilePath);

            var prof = Json.Obj(Json.Parse(File.ReadAllText(profilePath)));
            string mainClass = Json.Str(prof, "mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient");

            string java = FabricApi.FindJava(Manifest.JavaPath);
            if (java == null)
                throw new Exception("Java не найдена. Поставь Java 17+ (для 1.21.x) или Java 8 (для 1.16.5), " +
                                    "либо пропиши \"javaPath\" в launcher.json.");

            var cpFiles = new List<string>();
            foreach (var lo in Json.ArrOf(prof, "libraries"))
            {
                var ld = Json.Obj(lo);
                if (ld == null) continue;
                string name = Json.Str(ld, "name");
                if (string.IsNullOrEmpty(name)) continue;
                string libFile = Path.Combine(mcDir, "libraries",
                    MavenToPath(name).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(libFile)) cpFiles.Add(libFile);
            }

            string inherits = Json.Str(prof, "inheritsFrom", pack.Version);
            string clientJar = Path.Combine(mcDir, "versions", inherits, inherits + ".jar");
            if (!File.Exists(clientJar))
                throw new Exception("Нет client.jar версии " + inherits + " — выполни install (он качает vanilla).");
            cpFiles.Add(clientJar);

            string natives = Path.Combine(mcDir, "versions", profileId, "natives");
            string classpath = "\"" + string.Join(";", cpFiles) + "\"";
            string nick = "Player_" + new Random().Next(1000, 9999); // оффлайн-ник, без авторизации

            var tokens = new Dictionary<string, string>
            {
                { "${auth_player_name}", nick },
                { "${version_name}", profileId },
                { "${game_directory}", mcDir },
                { "${assets_root}", Path.Combine(mcDir, "assets") },
                { "${game_assets}", Path.Combine(mcDir, "assets", "virtual", "legacy") },
                { "${assets_index_name}", GetAssetIndex(mcDir, inherits) },
                { "${auth_uuid}", OfflineUuid(nick) },
                { "${auth_access_token}", "0" },
                { "${auth_xuid}", "0" },
                { "${clientid}", Guid.NewGuid().ToString() },
                { "${user_type}", "legacy" },
                { "${version_type}", "Fabric" },
                { "${natives_directory}", natives },
                { "${launcher_name}", "LoaderCLI" },
                { "${launcher_version}", "1.0" },
                { "${classpath}", classpath }
            };

            var argLine = "";
            foreach (var a in Manifest.JvmArgs) argLine += Quoted(a) + " ";
            argLine += "-Djava.library.path=" + Quoted(natives) + " ";
            argLine += "-cp " + classpath + " " + mainClass;
            foreach (var t in BuildGameArgs(prof))
                argLine += " " + Quoted(Substitute(t, tokens));

            _log("* запускаю " + profileId + " (оффлайн-ник " + nick + ")");
            var p = Process.Start(new ProcessStartInfo(java) { Arguments = argLine, UseShellExecute = false });
            _log("* Minecraft запущен, PID=" + p.Id);
        }

        public static IEnumerable<string> BuildGameArgs(Dictionary<string, object> prof)
        {
            var argsObj = Json.Obj(prof != null && prof.ContainsKey("arguments") ? prof["arguments"] : null);
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

        public static string Substitute(string token, Dictionary<string, string> map)
        {
            string v;
            return map.TryGetValue(token, out v) ? v : token;
        }

        public static string Quoted(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool need = s.IndexOf(' ') >= 0 || s.IndexOf(';') >= 0 || s.IndexOf('"') >= 0;
            s = s.Replace("\"", "\\\"");
            return need ? "\"" + s + "\"" : s;
        }

        public static string MavenToPath(string name)
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

        public static string GetAssetIndex(string mcDir, string version)
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

        public static string OfflineUuid(string name)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
                hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
                hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
                return new Guid(hash).ToString("N");
            }
        }
    }
}
