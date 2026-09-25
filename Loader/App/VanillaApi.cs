using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace Loader
{
    /// <summary>
    /// Скачивание vanilla-файлов Minecraft напрямую с Mojang (launcher meta + client jar + assets),
    /// чтобы профиль версии был в .minecraft даже без официального лаунчера.
    /// </summary>
    public static class VanillaApi
    {
        private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        public static void EnsureVanilla(string mcDir, string gameVersion, Action<string> log)
        {
            string profilePath = Path.Combine(mcDir, "versions", gameVersion, gameVersion + ".json");
            if (File.Exists(profilePath))
            {
                log("  [vanilla] профиль " + gameVersion + " уже есть.");
                return;
            }

            log("  [vanilla] ищу версию " + gameVersion + " в манифесте Mojang...");
            var root = Json.Obj(Json.Parse(Downloader.ReadAllText(VersionManifestUrl, default(CancellationToken))));
            string versionJsonUrl = null;
            foreach (var o in Json.ArrOf(root, "versions"))
            {
                var d = Json.Obj(o);
                if (Json.Str(d, "id") == gameVersion) { versionJsonUrl = Json.Str(d, "url"); break; }
            }
            if (string.IsNullOrEmpty(versionJsonUrl))
                throw new Exception("Версия " + gameVersion + " не найдена в списке Mojang.");

            Directory.CreateDirectory(Path.GetDirectoryName(profilePath));

            log("  [vanilla] скачиваю профиль версии...");
            string profileText = Downloader.ReadAllText(versionJsonUrl, default(CancellationToken));
            File.WriteAllText(profilePath, profileText);

            var vj = Json.Obj(Json.Parse(profileText));
            var dl = Json.Obj(vj != null && vj.ContainsKey("downloads") ? vj["downloads"] : null);
            var clientObj = Json.Obj(dl != null && dl.ContainsKey("client") ? dl["client"] : null);
            string clientUrl = clientObj != null ? Json.Str(clientObj, "url") : "";

            string jarPath = Path.Combine(mcDir, "versions", gameVersion, gameVersion + ".jar");
            if (!File.Exists(jarPath))
            {
                log("  [vanilla] скачиваю client.jar (" + gameVersion + ")...");
                Downloader.DownloadFile(clientUrl, jarPath, null, default(CancellationToken));
            }

            // assets index
            var ai = Json.Obj(vj != null && vj.ContainsKey("assetIndex") ? vj["assetIndex"] : null);
            string assetIndexUrl = ai != null ? Json.Str(ai, "url") : "";
            string assetIndexId = ai != null ? Json.Str(ai, "id") : "";
            if (!string.IsNullOrEmpty(assetIndexUrl))
            {
                string idxPath = Path.Combine(mcDir, "assets", "indexes", assetIndexId + ".json");
                if (!File.Exists(idxPath))
                {
                    log("  [vanilla] скачиваю индекс ресурсов " + assetIndexId + "...");
                    Downloader.DownloadFile(assetIndexUrl, idxPath, null, default(CancellationToken));
                    DownloadAssets(mcDir, idxPath, log);
                }
            }

            // libraries из профиля — качаем по их URL (natives пропускаем, они маленькие и по правилам Mojang)
            log("  [vanilla] скачиваю библиотеки...");
            int total = 0, ok = 0;
            foreach (var lo in Json.ArrOf(vj, "libraries"))
            {
                var ld = Json.Obj(lo);
                if (ld == null) continue;
                var dlObj = Json.Obj(ld.ContainsKey("download") ? ld["download"] : null);
                var art = dlObj != null && dlObj.ContainsKey("artifact") ? Json.Obj(dlObj["artifact"]) : null;
                if (art == null) continue;
                total++;
                string url = Json.Str(art, "url");
                string rel = Json.Str(art, "path");
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(rel)) continue;
                string dest = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (!File.Exists(dest))
                        Downloader.DownloadFile(url, dest, null, default(CancellationToken));
                    ok++;
                }
                catch (Exception ex)
                {
                    log("    ! библиотека: " + Path.GetFileName(dest) + " — " + ex.Message);
                }
            }
            log("  [vanilla] библиотеки: " + ok + "/" + total);
        }

        private static void DownloadAssets(string mcDir, string indexPath, Action<string> log)
        {
            try
            {
                var idx = Json.Obj(Json.Parse(File.ReadAllText(indexPath)));
                var objects = Json.Obj(idx != null && idx.ContainsKey("objects") ? idx["objects"] : null);
                if (objects == null) return;
                int n = 0;
                foreach (var kv in objects)
                {
                    var od = Json.Obj(kv.Value);
                    string hash = Json.Str(od, "hash");
                    if (string.IsNullOrEmpty(hash)) continue;
                    string dir = Path.Combine(mcDir, "assets", "objects", hash.Substring(0, 2));
                    string dest = Path.Combine(dir, hash);
                    if (File.Exists(dest)) continue;
                    string url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash;
                    try { Downloader.DownloadFile(url, dest, null, default(CancellationToken)); n++; }
                    catch { /* не критично */ }
                }
                log("  [vanilla] ресурсы: скачано объектов — " + n);
            }
            catch (Exception ex)
            {
                log("  [vanilla] ! объекты не скачаны: " + ex.Message);
            }
        }
    }
}
