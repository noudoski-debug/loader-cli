using System;
using System.Collections.Generic;
using System.IO;

namespace Loader
{
    /// <summary>Чтение/запись launcher.json и state.json (через собственный Json-парсер).</summary>
    public static class LauncherConfig
    {
        public static LauncherManifest Load(string path)
        {
            var m = new LauncherManifest();
            if (!File.Exists(path)) return m;

            var root = Json.Obj(Json.Parse(File.ReadAllText(path)));
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

        public static void SaveDefaultIfMissing(string path)
        {
            if (File.Exists(path)) return;
            File.WriteAllText(path, DefaultJson);
        }

        public static LoaderState LoadState(string path)
        {
            var s = new LoaderState();
            if (!File.Exists(path)) return s;
            try
            {
                var root = Json.Obj(Json.Parse(File.ReadAllText(path)));
                s.ManifestUrl = Json.Str(root, "manifestUrl", "");
                s.SelectedPack = Json.Str(root, "selectedPack", "");
            }
            catch { /* повреждён — начинаем с чистого */ }
            return s;
        }

        public static void SaveState(string path, LoaderState s)
        {
            var d = new Dictionary<string, object>
            {
                { "manifestUrl", s.ManifestUrl ?? "" },
                { "selectedPack", s.SelectedPack ?? "" }
            };
            File.WriteAllText(path, Json.Write(d));
        }

        private const string DefaultJson = @"{
  ""minecraftDir"": """",
  ""javaPath"": """",
  ""jvmArgs"": [ ""-Xmx4G"" ],
  ""packs"": [
    {
      ""name"": ""ExampleModpack"",
      ""url"": ""https://example.com/files/example-modpack.zip"",
      ""version"": ""1.21.4"",
      ""useFabric"": true
    },
    {
      ""name"": ""OldSchool"",
      ""url"": ""https://example.com/files/oldschool-1.16.5.zip"",
      ""version"": ""1.16.5"",
      ""useFabric"": true
    },
    {
      ""name"": ""Готовый установщик (exe)"",
      ""url"": ""https://example.com/files/setup.exe"",
      ""version"": ""1.21.11"",
      ""useFabric"": false
    }
  ]
}";
    }
}
