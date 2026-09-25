using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Loader
{
    /// <summary>
    /// Точка входа консольного лоадера (LoaderCLI.exe).
    ///
    /// Примеры:
    ///   LoaderCLI list  https://raw.githubusercontent.com/USER/REPO/main/launcher.json
    ///   LoaderCLI install 2                 (установить сборку №2 из последнего манифеста)
    ///   LoaderCLI play  "My Modpack 1.21.4" (запустить оффлайн, без авторизации)
    ///   LoaderCLI gui                       (открыть обычное оконное окно)
    /// </summary>
    internal static class CliProgram
    {
        private static string BaseDir;
        private static string StatePath;
        private static Engine _engine;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Downloader.InitTls();

            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            StatePath = Path.Combine(BaseDir, "state.json");

            if (args == null || args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return args == null || args.Length == 0 ? 1 : 0;
            }

            string cmd = args[0].ToLowerInvariant();
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++) rest.Add(args[i]);

            try
            {
                switch (cmd)
                {
                    case "gui":
                        System.Windows.Forms.Application.EnableVisualStyles();
                        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                        System.Windows.Forms.Application.Run(new MainForm(rest.ToArray()));
                        return 0;

                    case "list":     return CmdList(rest);
                    case "install":  return CmdInstall(rest);
                    case "play":     return CmdPlay(rest);
                    case "run":      return CmdRun(rest);
                    case "vanilla":  return CmdVanilla(rest);
                    case "folder":   return CmdFolder(rest);
                    default:
                        Err("неизвестная команда: " + cmd);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Err(ex.Message);
                return 1;
            }
        }

        private static bool IsHelp(string s)
        {
            return s == "/?" || s == "-h" || s == "--help" || s == "help";
        }

        // ============================ КОМАНДЫ ============================

        private static int CmdList(List<string> rest)
        {
            var e = GetEngine(rest);
            Console.WriteLine();
            Console.WriteLine("манифест: " + e.SourceName);
            Console.WriteLine("№   Название                         Версия    Fabric Тип  Статус");
            Console.WriteLine(new string('-', 92));
            for (int i = 0; i < e.Manifest.Packs.Count; i++)
            {
                var p = e.Manifest.Packs[i];
                Console.WriteLine(
                    Pad((i + 1).ToString(), 4) +
                    Pad(p.Name, 33) +
                    Pad(p.Version, 10) +
                    Pad(p.UseFabric ? "да" : "нет", 7) +
                    Pad(Engine.GuessType(p.Url), 5) +
                    e.PackStatus(p));
            }
            Console.WriteLine();
            Console.WriteLine("установка:  LoaderCLI install <№|название> [--url <ссылка на json>]");
            return 0;
        }

        private static int CmdInstall(List<string> rest)
        {
            string url = PopUrl(rest);
            var e = GetEngine(rest, url);

            // «install *» — поставить все сборки из манифеста
            if (rest.Count > 0 && (rest[0] == "*" || rest[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var p in e.Manifest.Packs) e.InstallPack(p);
                SaveRemote(url != null ? url : e.SourceName);
                return 0;
            }

            var pack = PickPack(e, rest);
            if (pack == null) return 2;
            e.InstallPack(pack);
            SaveRemote(url != null ? url : e.SourceName);
            return 0;
        }

        private static int CmdPlay(List<string> rest)
        {
            string url = PopUrl(rest);
            var e = GetEngine(rest, url);
            var pack = PickPack(e, rest);
            if (pack == null) return 2;
            e.LaunchGame(pack);
            return 0;
        }

        /// <summary>install + play одной командой.</summary>
        private static int CmdRun(List<string> rest)
        {
            int r = CmdInstall(rest);
            if (r != 0) return r;
            // rest уже пуст (PickPack его выпотрошил) — читаем тот же манифест заново
            var e = EnsureEngine(null);
            var sb = new List<string>();
            Console.Write("название/№ сборки для запуска: ");
            string sel = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(sel)) sb.Add(sel.Trim());
            var pack = PickPack(e, sb);
            if (pack == null) return 2;
            e.LaunchGame(pack);
            return 0;
        }

        private static int CmdVanilla(List<string> rest)
        {
            var e = EnsureEngine(null);
            string version = rest.Count > 0 ? rest[0] : Ask("версия Minecraft (1.21.4 / 1.21.11 / 1.16.5):");
            if (string.IsNullOrWhiteSpace(version)) return 2;
            VanillaApi.EnsureVanilla(e.MinecraftDir(), version.Trim(), Log);
            Console.WriteLine("готово: vanilla " + version + " в " + e.MinecraftDir());
            return 0;
        }

        private static int CmdFolder(List<string> rest)
        {
            var e = EnsureEngine(null);
            Directory.CreateDirectory(e.DownloadsDir);
            try { Process.Start("explorer.exe", e.DownloadsDir); } catch { }
            Console.WriteLine(e.DownloadsDir);
            return 0;
        }

        // ============================ ХЕЛПЕРЫ ============================

        private static Engine GetEngine(List<string> rest, string forcedUrl = null)
        {
            string url = forcedUrl;
            // если первым аргументом идёт ссылка — используем её как источник манифеста
            if (url == null && rest.Count > 0 && Engine.IsUrl(rest[0]))
            {
                url = rest[0];
                rest.RemoveAt(0);
            }
            var e = EnsureEngine(url);
            if (url != null) SaveRemote(url);
            return e;
        }

        private static Engine EnsureEngine(string url)
        {
            if (_engine != null && url == null) return _engine;
            var e = new Engine(BaseDir, Log);
            if (string.IsNullOrEmpty(url))
            {
                try { e.LoadManifest(LoadRemote()); }
                catch (Exception ex)
                {
                    Console.WriteLine("! удалённый манифест недоступен (" + ex.Message + "), беру локальный launcher.json");
                    e.LoadManifest("");
                }
            }
            else e.LoadManifest(url);
            _engine = e;
            return e;
        }

        /// <summary>Выбор сборки по № или названию; если не указано — интерактивный вопрос.</summary>
        private static PackInfo PickPack(Engine e, List<string> rest)
        {
            if (e.Manifest.Packs.Count == 0)
            {
                Err("в манифесте нет ни одной сборки (проверь секцию \"packs\" в json)");
                return null;
            }

            string sel = null;
            if (rest.Count > 0)
            {
                sel = string.Join(" ", rest.ToArray());
                rest.Clear();
            }
            if (string.IsNullOrWhiteSpace(sel))
                sel = Ask("название или № сборки ('*' — все):");
            if (string.IsNullOrWhiteSpace(sel)) return null;
            sel = sel.Trim();

            if (sel == "*" || sel.ToLowerInvariant() == "all")
            {
                Err("для запуска выбери одну сборку (№ или название)");
                return null;
            }

            int num;
            if (int.TryParse(sel, out num) && num >= 1 && num <= e.Manifest.Packs.Count)
                return e.Manifest.Packs[num - 1];

            foreach (var p in e.Manifest.Packs)
                if (string.Equals(p.Name, sel, StringComparison.OrdinalIgnoreCase))
                    return p;
            foreach (var p in e.Manifest.Packs)
                if ((p.Name ?? "").IndexOf(sel, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;

            Err("сборка не найдена: " + sel);
            return null;
        }

        private static string PopUrl(List<string> rest)
        {
            for (int i = 0; i < rest.Count - 1; i++)
            {
                if (rest[i] == "--url" || rest[i] == "-u")
                {
                    string v = rest[i + 1];
                    rest.RemoveAt(i + 1);
                    rest.RemoveAt(i);
                    return v;
                }
            }
            return null;
        }

        private static string LoadRemote()
        {
            try
            {
                var st = LauncherConfig.LoadState(StatePath);
                return st.ManifestUrl;
            }
            catch { return null; }
        }

        private static void SaveRemote(string source)
        {
            try
            {
                if (!Engine.IsUrl(source)) return;
                var st = LauncherConfig.LoadState(StatePath);
                st.ManifestUrl = source;
                LauncherConfig.SaveState(StatePath, st);
            }
            catch { }
        }

        private static string Ask(string question)
        {
            Console.Write(question + " ");
            try { return Console.ReadLine(); } catch { return null; }
        }

        private static void Log(string s) { Console.WriteLine(s); }
        private static void Err(string s) { Console.WriteLine("! " + s); }

        private static string Pad(string s, int w)
        {
            s = s ?? "";
            if (s.Length > w - 1) s = s.Substring(0, w - 2) + "…";
            return s.PadRight(w);
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("LoaderCLI — загрузчик сборок Minecraft (без авторизации)");
            Console.WriteLine("json-манифест можно хранить на GitHub и качать по raw-ссылке.");
            Console.WriteLine();
            Console.WriteLine("использование: LoaderCLI <команда> [аргументы]");
            Console.WriteLine();
            Console.WriteLine("  list [url|launcher.json]          показать список сборок из json");
            Console.WriteLine("  install <№|название> [url]        скачать zip/exe, распаковать,");
            Console.WriteLine("                                    положить jar-моды в mods/, поставить Fabric под версию");
            Console.WriteLine("  play <№|название> [url]           запустить игру оффлайн (случайный ник)");
            Console.WriteLine("  run [url]                          install + play одной командой");
            Console.WriteLine("  vanilla <версия>                  докачать vanilla-файлы версии (Mojang API)");
            Console.WriteLine("  folder                             открыть папку downloads");
            Console.WriteLine("  gui [url]                          оконный режим");
            Console.WriteLine();
            Console.WriteLine("примеры:");
            Console.WriteLine("  LoaderCLI list https://raw.githubusercontent.com/user/repo/main/launcher.json");
            Console.WriteLine("  LoaderCLI install 1");
            Console.WriteLine("  LoaderCLI play \"My Modpack 1.21.4\"");
            Console.WriteLine();
            Console.WriteLine("url сохраняется в state.json — дальше можно вызывать команды без ссылки.");
        }
    }
}
