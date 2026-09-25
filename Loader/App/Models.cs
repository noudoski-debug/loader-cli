using System.Collections.Generic;

namespace Loader
{
    /// <summary>Корень файла launcher.json.</summary>
    public class LauncherManifest
    {
        /// <summary>Путь к папке Minecraft (.minecraft). Пусто = %APPDATA%\.minecraft</summary>
        public string MinecraftDir = "";

        /// <summary>Путь/команда Java. Пусто = искать автоматически.</summary>
        public string JavaPath = "";

        /// <summary>Аргументы JVM, например ["-Xmx4G"].</summary>
        public List<string> JvmArgs = new List<string>();

        /// <summary>Список модпаков для скачивания.</summary>
        public List<PackInfo> Packs = new List<PackInfo>();
    }

    /// <summary>Один пункт в .json: Название - url - версия.</summary>
    public class PackInfo
    {
        public string Name = "";
        /// <summary>URL на .zip (сборка с модами) или .exe (готовый лаунчер/установщик).</summary>
        public string Url = "";
        /// <summary>Версия Minecraft: "1.21.4", "1.21.11", "1.16.5" ...</summary>
        public string Version = "";
        /// <summary>Ставить ли Fabric под эту версию (по умолчанию true).</summary>
        public bool UseFabric = true;
    }

    /// <summary>Компонент из профиля Fabric (loader / intermediary / launchmeta).</summary>
    public class FabricComponentFile
    {
        public string Client = "";
        public string Common = "";
        public string Server = "";
        public string Maven = "";
        public long Size = 0;
        public string Sha1 = "";
        public bool Local = false;
    }

    /// <summary>Компонент профиля fabric.</summary>
    public class FabricProfileComponent
    {
        public string Id = "";
        public string Category = "";      // "maven-maven", "fabric-loader", "game"...
        public int Priority = 0;
        public List<FabricComponentFile> Files = new List<FabricComponentFile>();
    }

    /// <summary>Ответ meta.fabricmc.net/v2/versions/loader/&lt;yarn&gt;/&lt;loader&gt;/profile</summary>
    public class FabricProfile
    {
        public string GameVersion = "";
        public string YarnVersion = "";
        public string LoaderVersion = "";
        public string LauncherMeta = "";
        public List<FabricProfileComponent> MainContainer = new List<FabricProfileComponent>();
        public List<FabricProfileComponent> NestedContainers = new List<FabricProfileComponent>();
    }

    /// <summary>Запись версии Fabric Loader (loader.fabricmc.net/v3).</summary>
    public class FabricLoaderEntry
    {
        public string Separator = "";
        public string Build = "";
        public string Version = "";
        public bool Stable = false;
    }

    /// <summary>Запись yarn-маппингов (yarn.fabricmc.net/v3).</summary>
    public class FabricYarnEntry
    {
        public string GameVersion = "";
        public string Mappings = "";       // "net.fabricmc:yarn:1.21.4+build.1:v2"
        public string Version = "";        // "1.21.4+build.1"
        public int Build = 0;
    }

    /// <summary>Настройки, которые лоадер сохраняет сам (state.json).</summary>
    public class LoaderState
    {
        public string ManifestUrl = "";
        public string SelectedPack = "";
    }
}
