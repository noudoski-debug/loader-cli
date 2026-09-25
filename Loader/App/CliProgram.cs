using System;
using System.Collections.Generic;
using System.IO;

namespace Loader
{
    /// <summary>
    /// LoaderCLI — консольный лоадер (только CMD, без окон и выбора).
    ///
    /// ССЫЛКА НА launcher.json ПРОПИСЫВАЕТСЯ В КОДЕ:
    ///   файл LauncherConfig.cs -> public const string RemoteManifestUrl = "https://...";
    /// Либо рядом с exe кладётся manifest.txt с одной строкой-ссылкой.
    ///
    /// Запуск без аргументов:  LoaderCLI.exe
    ///   1) качает launcher.json с GitHub;
    ///   2) показывает список сборок;
    ///   3) ждёт номер в консоли;
    ///   4) скачивает zip/exe, распаковывает, кладёт jar-моды в mods/,
    ///      ставит Fabric под версию из json (1.21.4 / 1.21.11 / 1.16.5 ...);
    ///   5) сам запускает Minecraft (оффлайн, без авторизации).
    /// </summary>
    internal static class CliProgram
    {
        private static string BaseDir;
        private static Engine _engine;   // кэш скачанного манифеста (не перезагружать при оффлайне)

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Downloader.InitTls();
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (args != null && args.Length > 0)
                return RunCommands(args);

            // ================= ГЛАВНЫЙ ЦИКЛ (без аргументов) =================
            PrintBanner();
            while (true)
            {
                var e = RefreshManifest();
                Console.WriteLine();
                Console.WriteLine("№   Название                             Версия   Тип  Статус");
                Console.WriteLine(new string('-', 78));
                for (int i = 0; i < e.Manifest.Packs.Count; i++)
                {
                    var p = e.Manifest.Packs[i];
                    Console.WriteLine(
                        Pad((i + 1).ToString(), 4) +
                        Pad(p.Name, 37) +
                        Pad(p.Version, 9) +
                        Pad(Engine.GuessType(p.Url), 5) +
                        e.PackStatus(p));
                }
                Console.WriteLine();
                Console.WriteLine("введи № сборки — лоадер скачает её и сам запустит игру.");
                Console.WriteLine("(Enter — обновить список, q — выход)");
                Console.Write("> ");

                string line;
                try { line = Console.ReadLine(); } catch { return 0; }
                if (line == null) return 0;
                line = line.Trim();
                if (line.ToLowerInvariant() == "q" || line.ToLowerInvariant() == "exit") return 0;
                if (line.Length == 0) continue;

                var pack = PickPack(e, line);
                if (pack == null) continue;

                try
                {
                    e.InstallPack(pack);          // скачал zip/exe -> распаковал -> модам место -> Fabric под версию
                    if (Engine.GuessType(pack.Url) == "EXE")
                    {
                        Log("* установщик .exe уже запущен — дальше сам.");
                    }
                    else
                    {
                        e.LaunchGame(pack);       // запустил Minecraft оффлайн, без авторизации
                    }
                }
                catch (Exception ex) { Err(ex.Message); }
                Console.WriteLine();
            }
        }

        // ============================ РЕЖИМ АРГУМЕНТОВ ============================

        private static int RunCommands(string[] args)
        {
            string cmd = (args[0] ?? "").ToLowerInvariant();
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++) rest.Add(args[i]);

            try
            {
                switch (cmd)
                {
                    case "list":
                        PrintList(RefreshManifest());
                        return 0;

                    case "install":
                    case "run":
                    {
                        var e = RefreshManifest();
                        string sel = rest.Count > 0 ? string.Join(" ", rest.ToArray()) : Ask("название или № сборки:");
                        var packs = PickPacks(e, sel);
                        if (packs.Count == 0) return 2;
                        foreach (var p in packs)
                        {
                            e.InstallPack(p);
                            if (cmd == "run" && Engine.GuessType(p.Url) != "EXE")
                                e.LaunchGame(p);
                        }
                        if (cmd == "install")
                            Console.WriteLine("установлено. запуск:  LoaderCLI run <№|название>  (или просто LoaderCLI.exe)");
                        return 0;
                    }

                    case "play":
                    {
                        var e = RefreshManifest();
                        string sel = rest.Count > 0 ? string.Join(" ", rest.ToArray()) : Ask("название или № сборки:");
                        var packs = PickPacks(e, sel);
                        if (packs.Count == 0) return 2;
                        e.LaunchGame(packs[0]);
                        return 0;
                    }

                    default:
                        Err("неизвестная команда: " + cmd);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex) { Err(ex.Message); return 1; }
        }

        // ============================ МАНИФЕСТ ============================

        /// <summary>
        /// Кидает launcher.json с GitHub (ссылка из кода / manifest.txt).
        /// Если сети нет — берёт последнюю скачанную копию, потом локальный launcher.json.
        /// </summary>
        private static Engine RefreshManifest()
        {
            var e = new Engine(BaseDir, Log);

            string url = LauncherConfig.ResolveRemoteUrl(BaseDir);
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    e.LoadManifest(url);
                    _engine = e;
                    return e;
                }
                catch (Exception ex)
                {
                    Err("удалённый манифест недоступен: " + ex.Message);
                }
            }

            // оффлайн: свежая копия из кэша
            if (_engine == null && File.Exists(e.CachePath))
            {
                try
                {
                    e.LoadManifest(e.CachePath);
                    Log("* работаю по сохранённой копии манифеста (нет сети).");
                    _engine = e;
                    return e;
                }
                catch { }
            }
            if (_engine != null) return _engine;

            // локальный launcher.json (если пустой — затираем шаблоном с примерами)
            string local = Path.Combine(BaseDir, "launcher.json");
            try
            {
                e.LoadManifest("");
                if (e.Manifest.Packs.Count == 0)
                {
                    LauncherConfig.OverwriteWithTemplate(local);
                    e.LoadManifest(local);
                    Log("* launcher.json был пустой — записан шаблон с примерами: " + local);
                }
            }
            catch (Exception ex)
            {
                Err("не удалось прочитать локальный launcher.json: " + ex.Message);
                LauncherConfig.OverwriteWithTemplate(local);
                try { e.LoadManifest(local); } catch { }
            }
            _engine = e;
            return e;
        }

        // ============================ ВЫБОР СБОРКИ ============================

        private static PackInfo PickPack(Engine e, string sel)
        {
            var list = PickPacks(e, sel);
            return list.Count > 0 ? list[0] : null;
        }

        private static List<PackInfo> PickPacks(Engine e, string sel)
        {
            var result = new List<PackInfo>();
            if (e.Manifest.Packs.Count == 0)
            {
                Err("в манифесте нет ни одной сборки (секция \"packs\" в launcher.json)");
                return result;
            }
            sel = (sel ?? "").Trim();
            if (sel.Length == 0) return result;

            if (sel == "*" || sel.Equals("all", StringComparison.OrdinalIgnoreCase))
                return new List<PackInfo>(e.Manifest.Packs);

            int num;
            if (int.TryParse(sel, out num) && num >= 1 && num <= e.Manifest.Packs.Count)
            {
                result.Add(e.Manifest.Packs[num - 1]);
                return result;
            }
            foreach (var p in e.Manifest.Packs)
                if (string.Equals(p.Name, sel, StringComparison.OrdinalIgnoreCase)) { result.Add(p); return result; }
            foreach (var p in e.Manifest.Packs)
                if ((p.Name ?? "").IndexOf(sel, StringComparison.OrdinalIgnoreCase) >= 0) { result.Add(p); return result; }

            Err("сборка не найдена: " + sel);
            return result;
        }

        // ============================ МЕЛОЧЬ ============================

        private static void PrintList(Engine e)
        {
            Console.WriteLine("манифест: " + e.SourceName);
            for (int i = 0; i < e.Manifest.Packs.Count; i++)
            {
                var p = e.Manifest.Packs[i];
                Console.WriteLine(Pad((i + 1).ToString(), 4) + Pad(p.Name, 37) +
                                  Pad(p.Version, 9) + Pad(Engine.GuessType(p.Url), 5) + e.PackStatus(p));
            }
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

        private static void PrintBanner()
        {
            Console.WriteLine("======================================================");
            Console.WriteLine("  LoaderCLI — загрузчик сборок Minecraft (без авторизации)");
            Console.WriteLine("  launcher.json качается с GitHub (ссылка в коде / manifest.txt)");
            Console.WriteLine("======================================================");
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("использование:");
            Console.WriteLine("  LoaderCLI.exe                      — список, вводишь № -> качает и запускает");
            Console.WriteLine("  LoaderCLI.exe list                 — только список");
            Console.WriteLine("  LoaderCLI.exe install <№|имя>      — скачать+установить ('*' = все)");
            Console.WriteLine("  LoaderCLI.exe run <№|имя>          — установить и запустить");
            Console.WriteLine("  LoaderCLI.exe play <№|имя>         — запустить установленное");
            Console.WriteLine();
            Console.WriteLine("ссылка на json задаётся в LauncherConfig.cs (RemoteManifestUrl)");
            Console.WriteLine("или файлом manifest.txt рядом с exe.");
        }
    }
}
