using System;
using System.Collections.Generic;
using System.IO;

namespace Loader
{
    /// <summary>
    /// Установка Fabric под конкретную версию Minecraft (1.21.4, 1.21.11, 1.16.5 ...).
    /// Использует официальные API: meta.fabricmc.net + maven.fabricmc.net.
    /// Ставит всё нужное в .minecraft: versions/, libraries/, и профиль fabric-loader-&lt;версия&gt;.json.
    /// </summary>
    public static class FabricApi
    {
        private const string MetaBase = "https://meta.fabricmc.net/v2/versions/";
        private const string MavenBase = "https://maven.fabricmc.net/";

        /// <summary>Последняя стабильная версия loader для gameVersion.</summary>
        public static string GetLatestLoader(string gameVersion)
        {
            string json = Downloader.ReadAllText(MetaBase + "loader/" + gameVersion, default(System.Threading.CancellationToken));
            var arr = Json.Arr(Json.Parse(json));
            if (arr == null || arr.Count == 0)
                throw new Exception("Fabric loader не найден для версии " + gameVersion);

            // список отсортирован по убыванию; берём первую stable, иначе первую
            foreach (var o in arr)
            {
                var d = Json.Obj(o);
                if (Json.Bool(d, "stable", false)) return Json.Str(d, "version");
            }
            return Json.Str(Json.Obj(arr[0]), "version");
        }

        /// <summary>Последние yarn-маппинги для gameVersion (для профиля).</summary>
        public static string GetLatestYarn(string gameVersion)
        {
            try
            {
                string json = Downloader.ReadAllText(MetaBase + "yarn/" + gameVersion, default(System.Threading.CancellationToken));
                var arr = Json.Arr(Json.Parse(json));
                if (arr != null && arr.Count > 0)
                    return Json.Str(Json.Obj(arr[0]), "version");
            }
            catch { }
            return gameVersion + "+build.1";
        }

        /// <summary>Скачивает один jar из профиля-компонента (client/common файл).</summary>
        private static void DownloadComponentFile(FabricComponentFile f, string mcDir, Action<string> log)
        {
            // fabric-loader: берём обычный universal jar без классификатора
            string url = !string.IsNullOrEmpty(f.Common) ? f.Common : f.Client;
            if (string.IsNullOrEmpty(url)) return;

            string relative = new Uri(url).AbsolutePath.TrimStart('/'); // net/fabricmc/.../fabric-loader-0.16.x.jar
            string dest = Path.Combine(mcDir, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) { log("      уже есть: " + Path.GetFileName(dest)); return; }

            log("      скачиваю: " + Path.GetFileName(dest));
            Downloader.DownloadFile(url, dest, null, default(System.Threading.CancellationToken));
        }

        /// <summary>Последняя версия fabric-installer с maven (список версий из maven-metadata.xml).</summary>
        public static string GetInstallerVersion()
        {
            try
            {
                string xml = Downloader.ReadAllText(MavenBase + "net/fabricmc/fabric-installer/maven-metadata.xml",
                    default(System.Threading.CancellationToken));
                // берём последний тег <version>x.y.z</version>
                string best = null;
                int pos = 0;
                while (true)
                {
                    int a = xml.IndexOf("<version>", pos, StringComparison.Ordinal);
                    if (a < 0) break;
                    int b = xml.IndexOf("</version>", a, StringComparison.Ordinal);
                    if (b < 0) break;
                    string v = xml.Substring(a + 9, b - a - 9).Trim();
                    if (v.Length > 0 && v[0] >= '0' && v[0] <= '9') best = v;
                    pos = b;
                }
                if (!string.IsNullOrEmpty(best)) return best;
            }
            catch { }
            return "1.1.2"; // заведомо существующая версия на случай недоступного maven
        }

        /// <summary>
        /// Скачивает официальный установщик Fabric Loader и запускает его в тихом режиме
        /// (--dir &lt;.minecraft&gt;), чтобы он сам поставил vanilla-профиль версии + все библиотеки Mojang.
        /// Возвращает true, если установка прошла успешно.
        /// </summary>
        public static bool RunOfficialInstaller(string gameVersion, string mcDir, Action<string> log)
        {
            try
            {
                string loaderVersion = GetLatestLoader(gameVersion);
                string installerVersion = GetInstallerVersion();
                string installerUrl = MavenBase + "net/fabricmc/fabric-installer/" + installerVersion +
                                      "/fabric-installer-" + installerVersion + ".jar";
                string jarPath = Path.Combine(Path.GetTempPath(), "fabric-installer-" + installerVersion + ".jar");

                if (!File.Exists(jarPath))
                {
                    log("  [Fabric] скачиваю официальный установщик " + installerVersion + "...");
                    Downloader.DownloadFile(installerUrl, jarPath, null, default(System.Threading.CancellationToken));
                }

                string java = FindJava();
                if (java == null)
                {
                    log("  [Fabric] ! Java не найдена — установщик не запущен.");
                    return false;
                }

                log("  [Fabric] запускаю установщик для " + gameVersion + " (тихий режим)...");
                var psi = new System.Diagnostics.ProcessStartInfo(java)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = "-jar \"" + jarPath + "\" install --dir \"" + mcDir +
                                "\" --download MINECRAFT --game " + gameVersion +
                                " --loader " + loaderVersion + " --side client --silent",
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    foreach (var line in (stdout + "\n" + stderr).Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length > 0) log("      | " + t);
                    }
                    if (p.ExitCode == 0)
                    {
                        log("  [Fabric] официальная установка завершена успешно.");
                        return true;
                    }
                    log("  [Fabric] установщик завершился с кодом " + p.ExitCode + ", ставлю профиль вручную...");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log("  [Fabric] ! установщик: " + ex.Message + " — ставлю профиль вручную...");
                return false;
            }
        }

        /// <summary>Ищет java.exe: JAVA_PATH env, PATH, стандартные каталоги JDK.</summary>
        public static string FindJava()
        {
            return FindJava(null);
        }

        public static string FindJava(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string c = Expand(configured);
                if (System.IO.File.Exists(c)) return c;
                string withExe = c.EndsWith(".exe") ? c : c + ".exe";
                if (System.IO.File.Exists(withExe)) return withExe;
            }

            foreach (var v in new[] { "JAVA_HOME", "JAVA_PATH" })
            {
                string dir = Environment.GetEnvironmentVariable(v);
                if (string.IsNullOrEmpty(dir)) continue;
                string cand = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(cand)) return cand;
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var seg in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(seg)) continue;
                try
                {
                    string cand = Path.Combine(seg.Trim(), "java.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new List<string>();
            foreach (var root in new[] { pf, pf + " (x86)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "BellSoft", "Zulu", "Semeru", "AdoptOpenJDK" })
                {
                    string dir = Path.Combine(root, vendor);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var jdk in Directory.GetDirectories(dir))
                        {
                            string exe = Path.Combine(jdk, "bin", "java.exe");
                            if (File.Exists(exe)) candidates.Add(exe);
                            // Minecraft runtime: jre-legacy / 17.0.x
                            foreach (var sub in new[] { "jre-legacy", "latest" })
                            {
                                string exe2 = Path.Combine(jdk, sub, "bin", "java.exe");
                                if (File.Exists(exe2)) candidates.Add(exe2);
                            }
                        }
                    }
                    catch { }
                }
            }
            // предпочитаем самые новые (сортировка по имени пути в обратном порядке работает для jdk-17/21)
            candidates.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
            return candidates.Count > 0 ? candidates[0] : null;
        }

        private static string Expand(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            return Environment.ExpandEnvironmentVariables(p);
        }


        /// <summary>
        /// Полная установка Fabric для версии. Возвращает id профиля, который можно запускать
        /// (например "fabric-loader-0.16.14-1.21.4").
        /// </summary>
        public static string Install(string mcDir, string gameVersion, Action<string> log)
        {
            // 1) Убеждаемся, что vanilla-файлы версии есть в .minecraft (нужны для запуска)
            try
            {
                VanillaApi.EnsureVanilla(mcDir, gameVersion, log);
            }
            catch (Exception ex)
            {
                log("  [vanilla] ! " + ex.Message);
            }

            // 2) Пробуем официальный установщик Fabric — он ставит всё сам корректно
            if (RunOfficialInstaller(gameVersion, mcDir, log))
            {
                string installedProfile = FindInstalledFabricProfile(mcDir, gameVersion);
                if (!string.IsNullOrEmpty(installedProfile)) return installedProfile;
            }

            // 3) Резервный путь: собираем профиль вручную через meta API
            log("  [Fabric] ручная сборка профиля...");
            log("  [Fabric] определяю последнюю версию loader для " + gameVersion + " ...");
            string loaderVersion = GetLatestLoader(gameVersion);
            string yarn = GetLatestYarn(gameVersion);
            log("  [Fabric] loader=" + loaderVersion + ", yarn=" + yarn);

            string profileUrl = MetaBase + "loader/" + yarn + "/" + loaderVersion + "/profile/" + gameVersion;
            var root = Json.Obj(Json.Parse(Downloader.ReadAllText(profileUrl, default(System.Threading.CancellationToken))));

            string profileId = Json.Str(root, "id", "fabric-loader-" + loaderVersion + "-" + gameVersion);
            string mainClass = Json.Str(root, "mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient");
            string launcherMeta = Json.Str(root, "launcherMeta");

            var components = new List<FabricProfileComponent>();
            CollectComponents(Json.ArrOf(root, "mainContainer"), components);
            CollectComponents(Json.ArrOf(root, "nestedContainers"), components);

            log("  [Fabric] скачиваю библиотеки (" + components.Count + " компонентов)...");
            foreach (var c in components)
                foreach (var f in c.Files)
                    DownloadComponentFile(f, mcDir, log);

            // Скачиваем сам launchMetadata (нужен для запуска клиента)
            if (!string.IsNullOrEmpty(launcherMeta))
            {
                string rel = new Uri(launcherMeta).AbsolutePath.TrimStart('/');
                string dest = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dest))
                {
                    log("      скачиваю: " + Path.GetFileName(dest));
                    Downloader.DownloadFile(launcherMeta, dest, null, default(System.Threading.CancellationToken));
                }
            }

            // Формируем vanilla-профиль этой версии (нужны его аргументы запуска)
            string vanillaProfilePath = Path.Combine(mcDir, "versions", gameVersion, gameVersion + ".json");
            if (!File.Exists(vanillaProfilePath))
                throw new FileNotFoundException(
                    "Не удалось получить vanilla-профиль " + gameVersion + ".\n" +
                    "Проверь интернет или запусти официальный лаунчер хотя бы раз.");

            var vanilla = Json.Obj(Json.Parse(File.ReadAllText(vanillaProfilePath)));

            // Собираем merged-профиль fabric-loader-X-Y.json
            var libs = new List<object>();
            AddLib(libs, launcherMeta);
            foreach (var c in components)
                foreach (var f in c.Files)
                    if (!string.IsNullOrEmpty(f.Client) || !string.IsNullOrEmpty(f.Common))
                        AddLibFromUrl(libs, !string.IsNullOrEmpty(f.Client) ? f.Client : f.Common);
            // наследуем библиотеки vanilla
            foreach (var l in Json.ArrOf(vanilla, "libraries")) libs.Add(l);

            string inherits = Json.Str(vanilla, "inheritsFrom", "");
            var profile = new Dictionary<string, object>
            {
                { "id", profileId },
                { "time", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") },
                { "releaseTime", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") },
                { "type", "release" },
                { "mainClass", mainClass },
                { "inheritsFrom", string.IsNullOrEmpty(inherits) ? gameVersion : inherits },
                { "libraries", libs },
                { "arguments", BuildArguments(vanilla, profileId) },
                { "assetIndex", vanilla != null && vanilla.ContainsKey("assetIndex") ? vanilla["assetIndex"] : null },
                { "assets", Json.Str(vanilla, "assets", "") }
            };

            string dir = Path.Combine(mcDir, "versions", profileId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, profileId + ".json"), Json.Write(profile));
            log("  [Fabric] профиль создан: versions/" + profileId + "/" + profileId + ".json");
            return profileId;
        }

        /// <summary>Ищет уже установленный профиль fabric-loader-*-gameVersion в versions/.</summary>
        public static string FindInstalledFabricProfile(string mcDir, string gameVersion)
        {
            try
            {
                string dir = Path.Combine(mcDir, "versions");
                if (!Directory.Exists(dir)) return null;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith("fabric-loader-") && name.EndsWith("-" + gameVersion))
                        return name;
                }
            }
            catch { }
            return null;
        }

        private static void CollectComponents(List<object> raw, List<FabricProfileComponent> outList)
        {
            foreach (var o in raw ?? new List<object>())
            {
                var d = Json.Obj(o);
                if (d == null) continue;
                var c = new FabricProfileComponent
                {
                    Id = Json.Str(d, "id"),
                    Category = Json.Str(d, "category"),
                    Priority = (int)Json.Num(d, "priority")
                };
                foreach (var fo in Json.ArrOf(d, "files"))
                {
                    var fd = Json.Obj(fo);
                    if (fd == null) continue;
                    c.Files.Add(new FabricComponentFile
                    {
                        Client = Json.Str(fd, "client"),
                        Common = Json.Str(fd, "common"),
                        Server = Json.Str(fd, "server"),
                        Maven = Json.Str(fd, "maven"),
                        Size = Json.Num(fd, "size"),
                        Sha1 = Json.Str(fd, "sha1"),
                        Local = Json.Bool(fd, "local")
                    });
                }
                outList.Add(c);
            }
        }

        private static void AddLib(List<object> libs, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            libs.Add(new Dictionary<string, object> { { "name", UrlToMaven(url) }, { "url", MavenBase } });
        }

        private static void AddLibFromUrl(List<object> libs, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            libs.Add(new Dictionary<string, object> { { "name", UrlToMaven(url) }, { "url", MavenBase } });
        }

        /// <summary>https://maven.../net/fabricmc/fabric-loader/0.16.14/fabric-loader-0.16.14.jar -> net.fabricmc:fabric-loader:0.16.14</summary>
        private static string UrlToMaven(string url)
        {
            string p = new Uri(url).AbsolutePath.TrimStart('/');
            int idx = p.IndexOf("/maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) p = p.Substring(0, idx);
            string[] seg = p.Split('/');
            if (seg.Length < 3) return p;
            string file = seg[seg.Length - 1];
            string version = seg[seg.Length - 2];
            string artifact = seg[seg.Length - 3];
            string group = string.Join(".", seg, 0, seg.Length - 3);
            // срезка классификаторов (-client/-common/.jar)
            string baseName = artifact + "-" + version;
            if (file.StartsWith(baseName) && file != baseName + ".jar")
            {
                string classifier = file.Substring(baseName.Length + 1).Replace(".jar", "");
                return group + ":" + artifact + ":" + version + ":" + classifier;
            }
            return group + ":" + artifact + ":" + version;
        }

        private static Dictionary<string, object> BuildArguments(Dictionary<string, object> vanilla, string profileId)
        {
            var args = new Dictionary<string, object>();
            var game = new List<object>();
            var jvm = new List<object>();

            if (vanilla != null)
            {
                var vArgs = Json.Obj(vanilla.ContainsKey("arguments") ? vanilla["arguments"] : null);
                if (vArgs != null)
                {
                    foreach (var g in Json.ArrOf(vArgs, "game")) game.Add(g);
                    foreach (var j in Json.ArrOf(vArgs, "jvm")) jvm.Add(j);
                }
                else
                {
                    // старый формат minecraft_args / minecraft_jvm_args (1.16.5 и т.п.)
                    if (vanilla.ContainsKey("minecraftArguments"))
                        foreach (var t in Json.Str(vanilla, "minecraftArguments").Split(' '))
                            if (!string.IsNullOrWhiteSpace(t)) game.Add(t);
                }
            }

            // добавляем свои токены, если их ещё нет
            if (!ContainsToken(game, "--username"))
            {
                game.Add("--username"); game.Add("${auth_player_name}");
                game.Add("--version"); game.Add("${version_name}");
                game.Add("--gameDir"); game.Add("${game_directory}");
                game.Add("--assetsDir"); game.Add("${assets_root}");
                game.Add("--assetIndex"); game.Add("${assets_index_name}");
                game.Add("--uuid"); game.Add("${auth_uuid}");
                game.Add("--accessToken"); game.Add("${auth_access_token}");
                game.Add("--userType"); game.Add("${user_type}");
                game.Add("--versionType"); game.Add("${version_type}");
            }

            args["game"] = game;
            args["jvm"] = jvm;
            return args;
        }

        private static bool ContainsToken(List<object> list, string token)
        {
            foreach (var o in list) if ((o as string) == token) return true;
            return false;
        }
    }
}
