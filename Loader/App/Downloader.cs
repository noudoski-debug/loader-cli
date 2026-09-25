using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

#if NET
// на .NET (Core) ZipFile/ZipArchiveExtensionMethods вынесены в отдельную сборку
using System.IO.Compression;
#endif

namespace Loader
{
    /// <summary>Загрузка файлов с прогрессом, докачка, распаковка zip.</summary>
    public static class Downloader
    {
        private const int BufferSize = 81920;

        public static void InitTls()
        {
            // .NET Framework по умолчанию может не включать TLS 1.2 — включаем всё доступное.
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.DefaultConnectionLimit = 16;
        }

        public static string GetFileNameFromUrl(string url)
        {
            try
            {
                string path = new Uri(url).AbsolutePath;
                string name = Path.GetFileName(path.TrimEnd('/'));
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "download.bin";
        }

        /// <summary>Скачивает url в destFile. progress: (прочитано байт, всего байт).</summary>
        public static void DownloadFile(string url, string destFile,
            Action<long, long> progress, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destFile));
            string tmp = destFile + ".part";

            long existing = 0;
            if (File.Exists(tmp)) existing = new FileInfo(tmp).Length;

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Loader/1.0";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            if (existing > 0) req.AddRange(existing);

            WebResponse resp;
            try
            {
                resp = req.GetResponse();
            }
            catch
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw;
            }

            using (resp)
            {
                long total = resp.ContentLength;              // -1 если неизвестен
                bool resumed = ((HttpWebResponse)resp).StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) existing = 0;                   // сервер не поддержал докачку — качаем заново

                using (var fs = new FileStream(tmp, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
                using (var input = resp.GetResponseStream())
                {
                    byte[] buf = new byte[BufferSize];
                    long done = existing;
                    int read;
                    while ((read = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        fs.Write(buf, 0, read);
                        done += read;
                        if (progress != null) progress(done, total > 0 ? total + existing : -1);
                    }
                }
            }

            if (File.Exists(destFile)) File.Delete(destFile);
            File.Move(tmp, destFile);
        }

        public static string ReadAllText(string url, CancellationToken token)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Loader/1.0";
            req.Timeout = 20000;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>Распаковывает zip в папку (перезаписывает файлы).</summary>
        public static void ExtractZip(string zipPath, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // директория
                    string dest = SafeCombine(targetDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
        }

        /// <summary>Защита от zip-slip ("..\..").</summary>
        private static string SafeCombine(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string rootFull = Path.GetFullPath(root);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Некорректный путь в архиве: " + relative);
            return full;
        }

        /// <summary>Запуск exe (отдельным процессом).</summary>
        public static Process StartExe(string exePath, string workDir)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = workDir,
                UseShellExecute = true
            };
            return Process.Start(psi);
        }
    }
}
