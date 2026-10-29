using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace MusicBoxMod
{
    // 玩家选择由游戏原生「Mod 配置」面板（mod.json 的 settings 声明）给出，每组第一项是总开关
    //   （关 = 整组回到游戏原声），组内条目用 visibleWhen 只在开关打开时显示：
    //   enableMenuGroup  → menuMainMenu / menuShop
    //   enableLevelGroup → menuLevelChoose / menuAlmanac
    // 音频一律从存档目录按**文件名约定**取，包内不携带任何音频：
    //   BGM 根目录 user://Seeleyuwo/rez/BGM/（可自由建子文件夹分类，递归扫描）：
    //     任意音频文件名都是一个可选曲目，值就是文件名（不含扩展名）；
    //     文件名正好是战斗 BGM 键（Grasswalk 等，见 BattleBgmKeys）时额外覆盖该键。
    //     主菜单/商店/选关/图鉴四项在面板里是下拉：mod.json 里只声明「原版（不替换）」这一项
    //     （宿主要求 enum 必须带非空 options），default 也是 off；
    //     模组每次扫完目录把自己安装包里的这四组 options 写成实际读到的曲目
    //     （见 SyncPackageDeclarations），所以刚丢进来的文件要等下次重启才进下拉。
    // 「关于」页是两段原生只读展示项（type: "text"），顺序为更新内容 → 作者与许可；
    //   三步用法写在 mod.json 的 playInstructions，由宿主显示在「Mod 管理 → 怎么玩」；
    //   「更新内容」的正文由模组从包内 changelog.txt 回写，更新记录只维护那一份。
    //   非音频扩展名的文件（如 .rez）一律不读。
    // 扫到的音频先按宿主的读取上限做一次「只看文件头」的预检（DescribeAudioRejection）：
    //   单文件 64MB；FLAC 还要求 8/16-bit、单声道或立体声、解码后不超过 128MB。
    //   预检不过的文件不进下拉，日志里点名说清原因和转格式的办法；.wma 这类游戏根本没有
    //   解码器的格式也照样说明一次。少了这一步，玩家只会看到「放了歌却没换」。
    //   格式按文件头认（DetectAudioExtension），不按文件名：网上不少歌曲名写着 .mp3、里面其实是
    //   FLAC，宿主只认扩展名，所以要拿真实格式去选解码器、也按真实格式判断能不能读。
    // 选中的槽找不到文件时，那个界面保持游戏原版并在日志里说明。丢文件进来不用重启：
    // 每秒的核对定时器会比对目录指纹，变了就整轮重载。
    // 所有作为“音乐”加载的音频都会强制循环播放。
    // 面板保存后 settings.Changed 会触发整轮重载并刷新已缓存的播放器，主菜单等处即时切歌。
    // 每次启动按「关于」页的「检测更新渠道」下拉走一次检查（关闭 / 官网 / Github，默认 Github）：
    // 版本号一律按严格 semver 比大小（预发布排在同号正式版之前，构建元数据不比）；
    // 有新版一律上报游戏的统一更新提醒（多 Mod 汇总成一份提醒；反射探测、编译期不依赖该 API；
    // 游戏版本较旧没有这套通道时只在日志里说明，不自带弹窗）。任何失败都只在日志里说明，不打扰玩家。
    public sealed partial class MusicBoxEntry : IXWModRuntimeEntry
    {
        private const string BgmRootVirtualPath = "user://Seeleyuwo/rez/BGM";
        private const string OffOption = "off";
        private const string MenuGroupSwitch = "enableMenuGroup";
        private const string LevelGroupSwitch = "enableLevelGroup";

        // 自己安装包的位置（SyncPackageDeclarations 要回写包内的声明）。
        private const string ModsVirtualPath = "user://Mods";
        private const string ManifestEntryName = "mod.json";
        private const string ChangelogEntryName = "changelog.txt";
        // 「关于」页里由包外内容撑起来的那一项：正文取 changelog 的最新版本块。
        private const string ChangelogSetting = "aboutChangelog";

        // 宿主 XWModExternalMediaLoader 认的音频扩展名（单文件上限 64MB）。
        private static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3", ".flac" };

        // 玩家常从网上拿到、但游戏根本没有解码器的扩展名：扫描时不认作曲目，只在日志里说明一次。
        private static readonly string[] UnscannedAudioExtensions =
        {
            ".wma", ".m4a", ".aac", ".opus", ".ape", ".wv", ".tta", ".aif", ".aiff", ".mid", ".midi",
        };

        // 宿主音频读取的硬上限，照搬 XWModExternalMediaLoader / XWManagedFlacDecoder 的规则：
        // 单文件 64MB；FLAC 还要求单声道或立体声、8/16-bit，且解码后 PCM（总样本×声道×2）不超过 128MB。
        // 超出的宿主直接拒绝，模组听不到声音，只能提前判出来并告诉玩家怎么改。
        private const long MaxAudioFileBytes = 64L * 1024L * 1024L;
        private const long MaxFlacDecodedPcmBytes = 128L * 1024L * 1024L;

        // 允许用文件名直接覆盖的战斗 BGM 键。界面那三个键（MainMenu、ChooseYourSeeds、
        // ZenGarden）由面板的曲目槽负责，不放进来，免得两处抢同一个键。
        private static readonly HashSet<string> BattleBgmKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Grasswalk", "GrasswalkDrums", "Moongrains", "WateryGraves", "RigorMormist",
            "GrazeTheRoof", "Zombotany", "Cerebraw", "UltimateBattle", "Loonboon",
            "BrainiacManiac", "FireMode", "PvzheMain",
        };

        // 面板项 -> 界面名（顺序与 mod.json 的 settings 声明一致）。
        private static readonly (string Setting, string Screen)[] ScreenSettings =
        {
            ("menuMainMenu", "MainMenu"),
            ("menuShop", "Shop"),
            ("menuLevelChoose", "LevelChoose"),
            ("menuAlmanac", "Almanac"),
            ("menuLevelEditorStage", "LevelEditorStage"),
            ("menuAwardSettlement", "AwardSettlement"),
        };

        // 面板项 -> 所属组的总开关键；开关为 false 时该项按「原版（不替换）」处理。
        private static readonly Dictionary<string, string> GroupSwitches =
            new(StringComparer.Ordinal)
            {
                ["menuMainMenu"] = MenuGroupSwitch,
                ["menuShop"] = MenuGroupSwitch,
                ["menuLevelChoose"] = LevelGroupSwitch,
                ["menuAlmanac"] = LevelGroupSwitch,
                ["menuLevelEditorStage"] = LevelGroupSwitch,
                ["menuAwardSettlement"] = LevelGroupSwitch,
            };

        private const string PlayerVisibleSetting = "showMenuPlayer";

        private enum ScreenMode
        {
            KeyOverride,
            SceneSwap,
            DialogHook
        }

        private sealed class ScreenDef
        {
            public ScreenMode Mode;
            public string GameKey;
            public string RestoreKey;
            public ScreenDef(ScreenMode mode, string gameKey, string restoreKey)
            { Mode = mode; GameKey = gameKey; RestoreKey = restoreKey; }
        }

        private static readonly Dictionary<string, ScreenDef> ScreenDefs =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["MainMenu"]    = new ScreenDef(ScreenMode.KeyOverride, "MainMenu", null),
                ["Almanac"]     = new ScreenDef(ScreenMode.SceneSwap, "ChooseYourSeeds", null),
                ["LevelChoose"] = new ScreenDef(ScreenMode.SceneSwap, "ZenGarden", null),
                ["LevelEditorStage"] = new ScreenDef(ScreenMode.SceneSwap, "ZenGarden", null),
                ["AwardSettlement"] = new ScreenDef(ScreenMode.SceneSwap, "ZenGarden", null),
                ["Shop"]        = new ScreenDef(ScreenMode.DialogHook, "MainMenu", "MainMenu"),
            };

        private sealed class SceneSwap
        {
            public string Screen;
            public string GameKey;
            public AudioStream Stream;
            public Resource Original;
            public bool HasOriginal;
            public bool Applied;
        }

        private sealed class DialogHook
        {
            public string Screen;
            public string PlayKey;
            public string StopKey;
            public string RestoreKey;
        }

        private XWModRuntimeContext _context;
        private XWModSettings _settings;
        private bool _settingsSubscribed;
        private bool _running;
        private int _generation;
        private SceneTreeTimer _panelTimer;
        private Action _panelTimeout;
        private string _pendingMusicStamp;
        private readonly Dictionary<Node, Action> _screenExits = new();
        private sealed class CachedAudio
        {
            public string Stamp;
            public AudioStream Stream;
        }
        private readonly Dictionary<string, CachedAudio> _audioCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AudioStream> _keyApplied = new();
        // 上一次应用时读到的面板值指纹，用于「通知没来但值已经变了」时兜底重载。
        private string _appliedSignature = "";
        // 面板落盘值的自取副本（见 SyncStoredPanelValues 的说明）。
        private readonly Dictionary<string, string> _panelScalar = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _panelBool = new(StringComparer.Ordinal);
        private DateTime _panelStamp = DateTime.MinValue;
        // 槽名（曲目文件名，不含扩展名）→ 磁盘绝对路径，每次扫描重建。
        private readonly Dictionary<string, string> _slots = new(StringComparer.OrdinalIgnoreCase);
        // 扫到但游戏读不了的曲目（槽名 → 原因）。不进下拉，玩家选中残留值时用它说明为什么保持原版。
        private readonly Dictionary<string, string> _blockedSlots = new(StringComparer.OrdinalIgnoreCase);
        // 同一首歌的同类提示一局只写一次，免得每秒核对时刷屏。
        private readonly HashSet<string> _warnedFiles = new(StringComparer.OrdinalIgnoreCase);
        private string _musicStamp = "";
        private readonly List<(string Key, AudioStream Stream)> _bgmOverrides = new();
        private readonly List<(string Screen, AudioStream Stream)> _menuEntries = new();

        private readonly List<SceneSwap> _sceneSwaps = new();
        private readonly List<DialogHook> _dialogHooks = new();
        private readonly List<string> _appliedMusicKeys = new();
        // 键覆盖类替换（界面 / 战斗 BGM）：记下被覆盖键的原版音频，玩家改回原版时当场还原。
        private readonly Dictionary<string, Resource> _keyOriginals = new();
        private readonly List<string> _ownedKeys = new();

        private SceneTree _tree;
        private bool _hooked;

        // 界面音乐续播：宿主对同一个界面键会连播两次（Loading.cs:391 起一次，MainMenu.cs:57 的
        // _Ready 再起一次，AudioManager.cs:233 无条件 Play(0)），听着就是每次进界面都从头开始。
        // 拦不住第二次调用，只能逐帧盯：同一个键同一个音频、位置突然回到开头，就接回原来的位置。
        // 战斗 BGM 不在名单里——重新开始才是它想要的效果。
        private static readonly HashSet<string> ResumeMusicKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "MainMenu", "ChooseYourSeeds", "ZenGarden",
        };
        private const float ResumeHeardFrom = 0.60f;      // 上次至少播到这么久才值得接回去
        private const float ResumeRestartedBelow = 0.30f; // 现在的位置低于它才算「回到开头」
        private const float ResumeLoopMargin = 0.35f;     // 距结尾不足这个差的是自然循环点，别接
        private readonly Dictionary<string, (AudioStream Stream, float Position)> _resumeSeen =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _resumeWatchFailed;
        // 已经写进安装包里的那份声明指纹（曲目 + 更新内容），用于避免每次核对目录都重写一遍包
        // （见 SyncPackageDeclarations）。
        private string _declaredSignature = "";
        private string _packageHash;

        public void Initialize(XWModRuntimeContext context)
        {
            if (_running)
                Shutdown();
            _context = context;
            _running = true;
            _generation++;
            // 初始化结束后宿主会冻结 Lifetime；预先登记统一清理，运行时不再向根作用域 Track。
            context.Lifetime.Track(Shutdown);
            PrepareHostUpdates();
            ReserveDialogAudioKeys();
            InitializeMenuPlayer();
            _panelStamp = DateTime.MinValue;
            _musicStamp = "";
            _pendingMusicStamp = null;
            _declaredSignature = "";
            _packageHash = null;
            _resumeWatchFailed = false;
            _warnedFiles.Clear();
            _panelScalar.Clear();
            _panelBool.Clear();
            try
            {
                _settings = context.Settings;
            }
            catch (Exception exception)
            {
                _context?.Warn("原生 Mod 配置不可用：" + exception.GetBaseException().Message);
            }
            try
            {
                LoadConfig();
            }
            catch (Exception exception)
            {
                context?.Warn("读取配置失败：" + exception.GetBaseException().Message);
            }
        }

        public void OnAllModsLoaded()
        {
            if (!_running || _hooked)
                return;
            try
            {
                RefreshStoredValues();
                ApplyRegistrations();
                HookSceneEvents();
                _context?.Log($"音乐盒 v{_context.ModVersion}：曲目={_slots.Count}，战斗覆盖={_bgmOverrides.Count}，界面替换={_menuEntries.Count}");
                SubscribeSettings();
                StartPanelWatch();
                SyncPackageDeclarations("启动");
                StartUpdateCheck();
            }
            catch (Exception exception)
            {
                _context?.Warn("应用音频替换失败：" + exception.GetBaseException().Message);
            }
        }

        public void Shutdown()
        {
            if (!_running)
                return;
            _running = false;
            _hostUpdates = null;
            _reportAvailable = null;
            _reportUpToDate = null;
            _generation++;
            if (GodotObject.IsInstanceValid(_panelTimer) && _panelTimeout != null)
                _panelTimer.Timeout -= _panelTimeout;
            _panelTimer = null;
            _panelTimeout = null;
            _menuPlayer?.Dispose();
            _playerScope?.Cleanup(); _playerMount = null; _playerScope = null;
            RestoreActiveDialogs();
            UnsubscribeSettings();
            UnhookSceneEvents();
            FinishUpdateRequest();
            foreach (SceneSwap swap in _sceneSwaps)
                RestoreSceneSwap(swap);
            var restoreKeys = new List<string>(_ownedKeys);
            RestoreMusicKeys(restoreKeys);
            foreach (string key in _appliedMusicKeys.Concat(restoreKeys).Distinct())
                if (!RefreshMusicMember(key))
                    StopMusicMember(key);
            _audioCache.Clear();
            _appliedMusicKeys.Clear();
            _slots.Clear();
            _blockedSlots.Clear();
            _settings = null;
            _bgmOverrides.Clear();
            _menuEntries.Clear();
            _sceneSwaps.Clear();
            _dialogHooks.Clear();
            _ownedKeys.Clear();
            _keyOriginals.Clear();
            _keyApplied.Clear();
        }

        // ---------- 配置加载 ----------

        private void LoadConfig()
        {
            if (_context == null)
                return;

            // 扫描和解码先放临时集合，目录不可读或复制未结束时不破坏已应用的状态。
            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var overrides = new List<(string Key, AudioStream Stream)>();
            string stamp = MusicStamp();

            string bgmDir = ProjectSettings.GlobalizePath(BgmRootVirtualPath);
            if (!Directory.Exists(bgmDir))
                WarnOnce("目录不存在", $"BGM 目录不存在，全部界面保持原版：{bgmDir}");

            // 两遍扫：先收游戏能读的，再处理读不了的。同一首两个版本同时放着时（比如 .flac + .mp3），
            // 能读的那份一定赢，不会因为文件名排序在前就被误报成「读不了」。
            var rejected = new List<(string Name, string File, string Why)>();
            string[] files = EnumerateFiles(bgmDir).OrderBy(file => file, StringComparer.Ordinal).ToArray();
            foreach (string file in files.Where(IsAudioFile))
            {
                string name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0)
                    continue;
                if (string.Equals(name, OffOption, StringComparison.OrdinalIgnoreCase))
                {
                    WarnOnce("保留曲名 " + file, $"{file} 使用了保留曲名 off，请重命名后再选择。");
                    continue;
                }
                string rejection = DescribeAudioRejection(file);
                if (rejection != null)
                {
                    rejected.Add((name, file, rejection));
                    continue;
                }
                if (!slots.TryAdd(name, file))
                {
                    WarnOnce("重名 " + file, $"{name} 有多份同名音频，用先扫到的 {slots[name]}，忽略 {file}");
                    continue;
                }
                // 文件名正好是战斗 BGM 键：它既是一个可选曲目，也顺手覆盖那个键。
                if (BattleBgmKeys.Contains(name))
                {
                    AudioStream stream = LoadAudio(file, loop: true);
                    if (stream == null)
                        throw new InvalidDataException("战斗音频暂时无法加载，保留当前播放状态：" + file);
                    overrides.Add((name, stream));
                }
            }
            foreach ((string name, string file, string why) in rejected)
            {
                if (slots.ContainsKey(name))
                    continue;
                blocked[name] = why;
                WarnOnce("屏蔽 " + name + " " + why,
                    $"{name} 游戏读不了（{why}），所以没列进选曲下拉：{file}。"
                    + "转成 16-bit 立体声、64MB 以内的文件就行（ogg / mp3 最省事）。");
            }

            // 游戏压根没有解码器的格式：不认作曲目，只说明一次，免得玩家纳闷文件为什么不在下拉里。
            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (!UnscannedAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;
                WarnOnce("格式 " + file,
                    $"{Path.GetFileName(file)} 是 {extension}，游戏没有这种格式的解码器，模组不会读取；"
                    + "转成 ogg 或 mp3 再放进来就能选。");
            }

            if (MusicStamp() != stamp)
                throw new IOException("音乐目录仍在写入，稍后重试。");
            _slots.Clear();
            foreach (var pair in slots) _slots.Add(pair.Key, pair.Value);
            _blockedSlots.Clear();
            foreach (var pair in blocked) _blockedSlots.Add(pair.Key, pair.Value);
            _bgmOverrides.Clear();
            _bgmOverrides.AddRange(overrides);
            _musicStamp = stamp;
        }

        // 递归扫描：玩家可以在 BGM 下自建子文件夹分类，非音频文件（如 .rez）自然被排除。
        private static IEnumerable<string> EnumerateFiles(string dir)
        {
            if (!Directory.Exists(dir))
                return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint });
        }

        private static bool IsAudioFile(string file) =>
            AudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<string> EnumerateAudioFiles(string dir)
        {
            return EnumerateFiles(dir)
                .Where(IsAudioFile)
                .OrderBy(file => file, StringComparer.Ordinal);
        }

        // 能不能被游戏读出来，只查文件头，不解码：宿主拒绝得一声不响，音乐直接回到原版，
        // 玩家只会看见「放了歌却没换」，所以这里提前把原因说清楚。
        // 返回 null 表示可以交给宿主读，否则返回中文原因。
        private static string DescribeAudioRejection(string file)
        {
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception exception)
            {
                return "文件读不到（" + exception.GetBaseException().Message + "）";
            }
            if (size <= 0)
                return "文件是空的";
            if (size > MaxAudioFileBytes)
                return $"文件 {size / 1048576L}MB，超过游戏能读的 {MaxAudioFileBytes / 1048576L}MB 上限";

            var head = new byte[64];
            int filled = ReadFileHead(file, head);
            if (filled < 0)
                return "文件打不开，游戏读不了";
            string extension = DetectAudioExtension(head, filled, Path.GetExtension(file));
            if (!extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                return null;

            string reason = DescribeFlacRejection(head, filled);
            if (reason == null)
                return null;
            // 网上很多「.mp3」其实是挂错名的 FLAC：原因里点名，玩家才知道该转码而不是改文件名。
            string declared = Path.GetExtension(file);
            return declared.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                ? reason
                : $"文件名写着 {declared}，里面其实是 FLAC：{reason}";
        }

        // 读文件头（够认格式、够取 FLAC 的 STREAMINFO 就行）。返回读到的字节数，打不开文件返回 -1。
        private static int ReadFileHead(string file, byte[] head)
        {
            int filled = 0;
            try
            {
                using var stream = File.OpenRead(file);
                while (filled < head.Length)
                {
                    int read = stream.Read(head, filled, head.Length - filled);
                    if (read <= 0)
                        break;
                    filled += read;
                }
            }
            catch
            {
                return -1;
            }
            return filled;
        }

        // 宿主只按扩展名挑解码器，所以「文件名是 .mp3、里面其实是 FLAC」这种挂错名的文件会被
        // 直接拒掉（报的还是 rejected .mp3，玩家根本看不出问题在哪）。这里按文件头认一次真实格式：
        // 认得出来就照真实的走（选解码器、判能不能读都用它），认不出来沿用文件名。
        private static string DetectAudioExtension(byte[] head, int filled, string declaredExtension)
        {
            if (Matches(head, filled, 0, (byte)'f', (byte)'L', (byte)'a', (byte)'C'))
                return ".flac";
            if (Matches(head, filled, 0, (byte)'O', (byte)'g', (byte)'g', (byte)'S'))
                return ".ogg";
            if (Matches(head, filled, 0, (byte)'R', (byte)'I', (byte)'F', (byte)'F')
                && Matches(head, filled, 8, (byte)'W', (byte)'A', (byte)'V', (byte)'E'))
                return ".wav";
            if (Matches(head, filled, 0, (byte)'I', (byte)'D', (byte)'3'))
                return ".mp3";
            // MPEG 帧头：11 位同步 + 版本位非 00。
            if (filled >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x18) != 0)
                return ".mp3";
            return declaredExtension;
        }

        private static bool Matches(byte[] head, int filled, int offset, params byte[] magic)
        {
            if (filled < offset + magic.Length)
                return false;
            for (int i = 0; i < magic.Length; i++)
            {
                if (head[offset + i] != magic[i])
                    return false;
            }
            return true;
        }

        // FLAC 的头：4 字节 "fLaC" + 元数据块，第一块必须是 STREAMINFO（34 字节）。
        // 偏移算法与宿主 XWManagedFlacDecoder 一致（第 10 字节起的 64 位大端：采样率 20 位、
        // 声道 3 位、位深 5 位、总样本 36 位）。
        private static string DescribeFlacRejection(byte[] head, int filled)
        {
            if (filled < 42)
                return "FLAC 文件头不完整或已损坏";
            if ((head[4] & 0x7F) != 0)
                return "FLAC 缺少 STREAMINFO 信息";
            if (head[5] != 0 || head[6] != 0 || head[7] < 34)
                return "FLAC 的 STREAMINFO 长度不对";

            ulong packed = 0;
            for (int offset = 18; offset < 26; offset++)
                packed = (packed << 8) | head[offset];
            int sampleRate = (int)(packed >> 44);
            int channels = (int)((packed >> 41) & 0x7) + 1;
            int bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
            long totalSamples = (long)(packed & 0xFFFFFFFFFUL);

            if (sampleRate <= 0 || sampleRate > 192000)
                return $"FLAC 采样率 {sampleRate} 游戏不支持";
            if (channels is not (1 or 2))
                return $"FLAC 是 {channels} 声道，游戏只读单声道或立体声";
            if (bitsPerSample is not (8 or 16))
                return $"FLAC 是 {bitsPerSample}-bit，游戏只读 8-bit 或 16-bit";
            long decoded = totalSamples * channels * 2L;
            if (totalSamples <= 0)
                return "FLAC 没写明时长信息";
            if (decoded > MaxFlacDecodedPcmBytes)
                return $"FLAC 解码后约 {decoded / 1048576L}MB，超过游戏的 {MaxFlacDecodedPcmBytes / 1048576L}MB 上限";
            return null;
        }

        // 同一首歌的同一句提示一局只写一次（目录每秒核对，重复写会刷屏）。
        private void WarnOnce(string identity, string message)
        {
            if (!_warnedFiles.Add(identity))
                return;
            _context?.Warn(message);
        }

        // 指纹包含完整路径、大小和时间；长度前缀避免文件名中的分隔符造成碰撞。
        private static string MusicStamp()
        {
            var stamp = new StringBuilder();
            foreach (string file in EnumerateAudioFiles(ProjectSettings.GlobalizePath(BgmRootVirtualPath)))
                stamp.Append(file.Length).Append(':').Append(file).Append(':').Append(AudioFileStamp(file)).Append(';');
            return stamp.ToString();
        }

        private static string AudioFileStamp(string file)
        {
            var info = new FileInfo(file);
            return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }

        // ---------- 面板选项 -> 音频 ----------

        // 依据原生 Mod 配置的当前值挑选音频。清掉上一轮的界面结果，战斗 BGM 覆盖保持独立。
        private void BuildSelections()
        {
            var selections = new List<(string Screen, AudioStream Stream)>();
            if (_settings == null)
            {
                _menuEntries.Clear();
                return;
            }

            foreach ((string setting, string screen) in ScreenSettings)
            {
                if (!ReadSwitch(setting))
                    continue;
                string track = ReadOption(setting);
                if (string.IsNullOrWhiteSpace(track) || track == OffOption)
                    continue;
                string file = SlotPath(track);
                if (file == null)
                {
                    if (_blockedSlots.TryGetValue(track, out string why))
                        WarnOnce("选曲屏蔽 " + screen + track + why, $"{track} 游戏读不了（{why}），界面 {screen} 保持原版。"
                                       + "转成 16-bit 立体声、64MB 以内的文件（ogg / mp3 最省事），重启游戏后重新选它。");
                    else
                        WarnOnce("选曲缺失 " + screen + track, $"{BgmRootVirtualPath}（含子文件夹）下没有 {track}.*，界面 {screen} 保持原版。");
                    continue;
                }
                AudioStream stream = LoadAudio(file, loop: true);
                if (stream == null)
                    throw new InvalidDataException("选中音频暂时无法加载，保留当前播放状态：" + file);
                selections.Add((screen, stream));
            }

            _menuEntries.Clear();
            _menuEntries.AddRange(selections);
        }

        // 面板当前生效值的可读指纹，既用于日志自证，也用于漂移检测。
        private string PanelSignature()
        {
            if (_settings == null)
                return "原生配置未激活";
            var parts = new List<string>();
            foreach ((string setting, string screen) in ScreenSettings)
                parts.Add($"{screen}={(ReadSwitch(setting) ? ReadOption(setting) ?? "读取失败" : "关闭")}");
            return string.Join(" ", parts);
        }

        private string ReadOption(string key)
        {
            if (_panelScalar.TryGetValue(key, out string adopted))
                return adopted?.Trim();
            try
            {
                return _settings.Get<string>(key)?.Trim();
            }
            catch (Exception exception)
            {
                WarnOnce("配置项 " + key, $"读取配置项失败 {key}：{exception.GetBaseException().Message}");
                return null;
            }
        }

        // 组总开关：优先用面板落盘副本，缺失时回落到宿主生效值（宿主对缺键会用声明的 default=true）。
        private bool ReadSwitch(string settingKey)
        {
            if (!GroupSwitches.TryGetValue(settingKey, out string switchKey))
                return true;
            if (_panelBool.TryGetValue(switchKey, out bool adopted))
                return adopted;
            try
            {
                return _settings.Get<bool>(switchKey);
            }
            catch (Exception exception)
            {
                WarnOnce("配置开关 " + switchKey, $"读取开关失败 {switchKey}：{exception.GetBaseException().Message}");
                return true;
            }
        }

        private string SlotPath(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot))
                return null;
            return _slots.TryGetValue(slot.Trim(), out string path) ? path : null;
        }

        private void AddLoadedStream(List<(string Key, AudioStream Stream)> target, string key, string file, bool loop)
        {
            AudioStream stream = LoadAudio(file, loop);
            if (stream != null)
                target.Add((key, stream));
        }

        private AudioStream LoadAudio(string absolute, bool loop)
        {
            if (string.IsNullOrWhiteSpace(absolute))
                return null;

            if (!File.Exists(absolute))
            {
                _context?.Warn("音频文件不存在：" + absolute);
                return null;
            }

            string stamp = AudioFileStamp(absolute);
            if (_audioCache.TryGetValue(absolute, out CachedAudio cached)
                && cached.Stamp == stamp && GodotObject.IsInstanceValid(cached.Stream))
                return cached.Stream;

            // 宿主按扩展名挑解码器，所以交给它的是文件内容真正的格式，不是文件名上写的那个。
            var head = new byte[12];
            int filled = Math.Max(0, ReadFileHead(absolute, head));
            string extension = Path.GetExtension(absolute);
            string realExtension = DetectAudioExtension(head, filled, extension);

            if (XWModExternalMediaLoader.TryLoadAudio(
                    absolute, realExtension, out AudioStream stream, out string diagnostic)
                && GodotObject.IsInstanceValid(stream))
            {
                if (AudioFileStamp(absolute) != stamp)
                    throw new IOException("音频仍在写入：" + absolute);
                if (loop)
                    ForceLoop(stream);
                _audioCache[absolute] = new CachedAudio { Stamp = stamp, Stream = stream };
                return stream;
            }

            WarnOnce("加载 " + absolute + stamp, $"音频加载失败 {absolute}：{diagnostic}。"
                           + (realExtension.Equals(extension, StringComparison.OrdinalIgnoreCase)
                               ? ""
                               : $"文件名写着 {extension}，里面其实是 {realExtension}；")
                           + "把这首歌转成 16-bit 立体声、64MB 以内的 ogg / mp3 再放进来就行。");
            return null;
        }

        private static void ForceLoop(AudioStream stream)
        {
            switch (stream)
            {
                case AudioStreamWav wav:
                    wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                    wav.LoopBegin = 0;
                    int frames = (int)Math.Round(wav.GetLength() * wav.MixRate);
                    if (frames > 0)
                        wav.LoopEnd = frames;
                    break;
                case AudioStreamOggVorbis ogg:
                    ogg.Loop = true;
                    break;
                case AudioStreamMP3 mp3:
                    mp3.Loop = true;
                    break;
            }
        }

        // ---------- 注册与应用 ----------

        private void ApplyRegistrations(bool rebuildSelections = true)
        {
            _menuPlayer?.ResetPlayback();
            // 内容结构在宿主加载完 Mod 后就冻结了，重载时不能再注册新键；
            // 所以本模组占用的键一律原地改写 AUDIOS，并记下原版音频供还原。
            if (rebuildSelections)
                BuildSelections();
            var ownedBefore = new List<string>(_ownedKeys);
            _ownedKeys.Clear();
            _appliedMusicKeys.Clear();

            foreach ((string key, AudioStream stream) in _bgmOverrides)
                if (SetMusicKey(key, stream))
                    _appliedMusicKeys.Add(key);

            foreach ((string screen, AudioStream stream) in _menuEntries)
            {
                ScreenDef def = ScreenDefs[screen];
                switch (def.Mode)
                {
                    case ScreenMode.KeyOverride:
                        if (SetMusicKey(def.GameKey, stream))
                            _appliedMusicKeys.Add(def.GameKey);
                        break;
                    case ScreenMode.SceneSwap:
                        _sceneSwaps.Add(new SceneSwap
                        {
                            Screen = screen,
                            GameKey = def.GameKey,
                            Stream = stream
                        });
                        _appliedMusicKeys.Add(def.GameKey);
                        break;
                    case ScreenMode.DialogHook:
                        string playKey = "MusicBox.Screen." + screen;
                        if (SetMusicKey(playKey, stream))
                        {
                            _dialogHooks.Add(new DialogHook
                            {
                                Screen = screen,
                                PlayKey = playKey,
                                StopKey = def.GameKey,
                                RestoreKey = def.RestoreKey
                            });
                            _appliedMusicKeys.Add(playKey);
                        }
                        break;
                }
            }

            // 这一轮被玩家改成「原版」的键：当场把 AUDIOS 还原，并刷新正在播的播放器。
            var dropped = ownedBefore.FindAll(key => !_ownedKeys.Contains(key));
            if (dropped.Count > 0)
            {
                RestoreMusicKeys(dropped);
                _appliedMusicKeys.AddRange(dropped);
            }

            _appliedSignature = PanelSignature();
            var used = new HashSet<AudioStream>(_bgmOverrides.Select(pair => pair.Stream).Concat(_menuEntries.Select(pair => pair.Stream)));
            foreach (string file in _audioCache.Keys.ToArray())
                if (!used.Contains(_audioCache[file].Stream))
                    _audioCache.Remove(file);
        }

        private bool SetMusicKey(string key, AudioStream stream)
        {
            var manager = ResourceManager.Instance;
            if (string.IsNullOrWhiteSpace(key) || stream == null || manager == null)
                return false;

            if (!_keyOriginals.ContainsKey(key)
                || (_keyApplied.TryGetValue(key, out AudioStream previous)
                    && manager.AUDIOS.TryGetValue(key, out Resource current) && !ReferenceEquals(current, previous)))
            {
                manager.AUDIOS.TryGetValue(key, out Resource original);
                _keyOriginals[key] = original;
            }
            if (manager.AUDIOS.ContainsKey(key))
            {
                manager.AUDIOS[key] = stream;
                _keyApplied[key] = stream;
                _ownedKeys.Add(key);
                return true;
            }
            // 注册表此时已冻结；缺键是初始化/宿主兼容问题，不在热重载时偷注册。
            WarnOnce("音频键缺失 " + key, "音频键未在初始化阶段就绪，跳过替换：" + key);
            _keyOriginals.Remove(key);
            return false;
        }

        private void RestoreMusicKeys(List<string> keys)
        {
            var manager = ResourceManager.Instance;
            if (manager == null)
                return;
            foreach (string key in keys)
            {
                // 其他模组已接管的键不再覆盖回本模组保存的旧值。
                if (_keyApplied.TryGetValue(key, out AudioStream applied)
                    && manager.AUDIOS.TryGetValue(key, out Resource current) && ReferenceEquals(current, applied))
                {
                    _keyOriginals.TryGetValue(key, out Resource original);
                    if (original == null)
                        manager.AUDIOS.Remove(key);
                    else
                        manager.AUDIOS[key] = original;
                }
                _keyOriginals.Remove(key);
                _keyApplied.Remove(key);
            }
        }

        private void RefreshAppliedMusicMembers()
        {
            foreach (string key in _appliedMusicKeys.Distinct())
                if (!RefreshMusicMember(key))
                    StopMusicMember(key);
        }

        private bool RefreshMusicMember(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;
            var audio = AudioManager.Instance;
            var manager = ResourceManager.Instance;
            if (audio == null || manager == null
                || !manager.AUDIOS.TryGetValue(key, out Resource resource)
                || resource is not AudioStream stream)
                return false;
            try
            {
                AudioStreamPlayerMember member = audio.MemberFind(key, AudioManagerEnum.TYPE.MUSIC);
                if (!GodotObject.IsInstanceValid(member))
                    return false;
                if (ReferenceEquals(member.Stream, stream))
                    return true;
                bool wasPlaying = member.IsPlaying();
                member.Stop();
                member.Stream = stream;
                if (wasPlaying)
                    member.Play();
                return true;
            }
            catch (Exception exception)
            {
                _context?.Warn($"刷新播放器缓存失败 {key}：{exception.GetBaseException().Message}");
                return false;
            }
        }

        private void StopMusicMember(string key)
        {
            try
            {
                var member = AudioManager.Instance?.MemberFind(key, AudioManagerEnum.TYPE.MUSIC);
                if (GodotObject.IsInstanceValid(member))
                {
                    member.Stop();
                    member.Stream = null;
                }
            }
            catch (Exception exception)
            {
                WarnOnce("停止播放器 " + key, $"停止播放器失败 {key}：{exception.GetBaseException().Message}");
            }
        }

        // 新内容只能在 Initialize 注册。先占位，之后设置切歌只替换已存在键的流。
        private void ReserveDialogAudioKeys()
        {
            foreach (var pair in ScreenDefs)
                if (pair.Value.Mode == ScreenMode.DialogHook)
                    Register("MusicBox.Screen." + pair.Key, new AudioStreamWav(), false);
        }

        private bool Register(string key, AudioStream stream, bool allowOverride)
        {
            if (_context == null || stream == null || string.IsNullOrWhiteSpace(key))
                return false;
            if (_context.TryRegister("Audio", key, Variant.From(stream), allowOverride, out string diagnostic))
            {
                return true;
            }
            _context.Warn($"注册音频失败 {key}：{diagnostic}");
            return false;
        }

        // ---------- 原生 Mod 配置热更新 ----------

        private void SubscribeSettings()
        {
            if (_settings == null || _settingsSubscribed)
                return;
            _settings.Changed += OnSettingsChanged;
            _settingsSubscribed = true;
        }

        private void UnsubscribeSettings()
        {
            if (_settings != null && _settingsSubscribed)
                _settings.Changed -= OnSettingsChanged;
            _settingsSubscribed = false;
        }

        // 面板保存后由主线程同步调用。这里不吞异常：让宿主把它计入“应用配置失败”，
        // 玩家会看到“配置已保存，但 Mod 应用配置时出错，请重启游戏”。
        private void OnSettingsChanged(IReadOnlyList<string> keys)
        {
            if (!_running)
                return;
            if (keys.Count > 0 && keys.All(key => key == PlayerVisibleSetting || key == "playbackOrder" || key == "playerAlwaysOnTop"))
            {
                _panelStamp = DateTime.MinValue;
                if (RefreshStoredValues()) _menuPlayer?.RefreshVisibility();
                return;
            }
            string before = _appliedSignature;
            if (Reload() && before != _appliedSignature)
                _context?.Log("音乐配置已生效：" + _appliedSignature);
        }

        // 宿主的 Changed 只在「包体一致 + 值真的变了」时推送，而它比对包体哈希用的是大小写敏感的 ==：
        // 管理快照的哈希来自 XWModInstallTransaction.TryHash（Convert.ToHexString，大写），
        // 运行中的包哈希在 ModLoader 里被 ToLowerInvariant 过（小写），两者永远对不上，
        // 于是面板保存只落盘、并按「部分设置重启游戏后生效」答复，immediate 项也收不到通知。
        // 值仍然写进了 user://ModSettings/<sha256(modId)>.json，所以我们自己核对这份文件。
        // （游戏源码 41e2193e64 起这处大小写已修，通知能正常来；这份自取副本留着兼容旧构建，
        //  也顺带承担「目录里丢了文件后不必重启」的核对。）
        private void SyncStoredPanelValues(string reason)
        {
            if (_settings == null || _context == null)
                return;
            string path = StoredPanelPath();
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) == _panelStamp)
                return;
            if (!RefreshStoredValues())
                return;
            string current = PanelSignature();
            if (current == _appliedSignature)
                return;
            if (Reload())
                _context?.Log("音乐配置已生效：" + _appliedSignature);
        }

        private string StoredPanelPath() => ProjectSettings.GlobalizePath(
            "user://ModSettings/" + StorageHash(_context.ModId) + ".json");

        // 把面板落盘的值读成模组自己的副本；宿主通知到达时也会先刷新，避免用到旧副本。
        private bool RefreshStoredValues()
        {
            if (_settings == null || _context == null)
                return true;
            string path = StoredPanelPath();
            if (!File.Exists(path))
            {
                _panelScalar.Clear();
                _panelBool.Clear();
                _panelStamp = DateTime.MinValue;
                return true;
            }
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _panelStamp)
                return true;
            if (!ReadStoredValues(path))
                return false;
            _panelStamp = stamp;
            return true;
        }

        private bool ReadStoredValues(string path)
        {
            // 解析成功并确认文件未继续变化后才提交；失败不清掉上次可用的配置。
            try
            {
                string stamp = AudioFileStamp(path);
                JsonNode root = JsonNode.Parse(File.ReadAllText(path));
                if (root is not JsonObject || root["values"] is not JsonObject values)
                    throw new InvalidDataException("缺少 values 对象");
                // 与宿主 XWModSettingsService.Read 的身份/版本规则一致。
                if (root["version"]?.GetValue<int>() != 1 || TextOf(root["modId"]) != _context.ModId.Trim().ToLowerInvariant())
                    throw new InvalidDataException("配置文件身份或版本不匹配");
                var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
                var switches = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var pair in values)
                {
                    if (pair.Value == null)
                        continue;
                    if ((GroupSwitches.ContainsValue(pair.Key) || pair.Key == PlayerVisibleSetting || pair.Key == "playerAlwaysOnTop") && pair.Value.GetValueKind() == JsonValueKind.True)
                        switches[pair.Key] = true;
                    else if ((GroupSwitches.ContainsValue(pair.Key) || pair.Key == PlayerVisibleSetting || pair.Key == "playerAlwaysOnTop") && pair.Value.GetValueKind() == JsonValueKind.False)
                        switches[pair.Key] = false;
                    else if (pair.Value.GetValueKind() == JsonValueKind.String && IsKnownOption(pair.Key, TextOf(pair.Value)))
                        scalars[pair.Key] = TextOf(pair.Value).Trim();
                }
                if (AudioFileStamp(path) != stamp)
                    throw new IOException("配置文件仍在写入");
                _panelScalar.Clear();
                foreach (var pair in scalars) _panelScalar.Add(pair.Key, pair.Value);
                _panelBool.Clear();
                foreach (var pair in switches) _panelBool.Add(pair.Key, pair.Value);
                return true;
            }
            catch (Exception exception)
            {
                WarnOnce("面板读取 " + exception.GetBaseException().Message,
                    "读取面板配置失败，保留当前状态并稍后重试：" + exception.GetBaseException().Message);
                return false;
            }
        }

        private bool IsKnownOption(string key, string option)
        {
            option = option?.Trim();
            if (string.IsNullOrEmpty(option))
                return false;
            // 界面曲目的下拉值就是文件名（不含扩展名），选项清单由本机目录生成，
            // 所以这里只认得键，具体能不能播交给 SlotPath 判定。
            if (key == "playbackOrder") return option == "sequence" || option == "reverse" || option == "shuffle" || option == "loop";
            return ScreenSettings.Any(pair => pair.Setting == key);
        }

        private static string StorageHash(string modId)
        {
            byte[] digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes((modId ?? "").Trim().ToLowerInvariant()));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        // 每秒看一眼面板有没有落盘（只比时间戳，不改文件就不解析）。
        private void StartPanelWatch()
        {
            if (!_running || _panelTimer != null || Engine.GetMainLoop() is not SceneTree tree)
                return;
            int generation = _generation;
            _panelTimer = tree.CreateTimer(1.0);
            _panelTimeout = () =>
            {
                if (!_running || generation != _generation)
                    return;
                _panelTimer = null;
                _panelTimeout = null;
                try { SyncStoredPanelValues("定时核对"); }
                catch (Exception exception) { WarnOnce("面板核对", "核对面板配置失败：" + exception.GetBaseException().Message); }
                try { MaybeRescanMusicLibrary(); }
                catch (Exception exception) { WarnOnce("目录核对", "核对音乐目录失败，将继续重试：" + exception.GetBaseException().Message); }
                StartPanelWatch();
            };
            _panelTimer.Timeout += _panelTimeout;
        }

        private bool Reload(bool rescanLibrary = false)
        {
            if (!_running || !RefreshStoredValues())
                return false;
            // 慢操作先完成，再拆旧钩子；扫描/加载异常时旧播放器仍可继续播放。
            var oldSlots = _slots.ToArray();
            var oldBlocked = _blockedSlots.ToArray();
            var oldOverrides = _bgmOverrides.ToArray();
            string oldStamp = _musicStamp;
            try
            {
                if (rescanLibrary)
                    LoadConfig();
                BuildSelections();
            }
            catch
            {
                _slots.Clear();
                foreach (var pair in oldSlots) _slots.Add(pair.Key, pair.Value);
                _blockedSlots.Clear();
                foreach (var pair in oldBlocked) _blockedSlots.Add(pair.Key, pair.Value);
                _bgmOverrides.Clear();
                _bgmOverrides.AddRange(oldOverrides);
                _musicStamp = oldStamp;
                throw;
            }
            RestoreActiveDialogs();
            UnhookSceneEvents();
            foreach (SceneSwap swap in _sceneSwaps)
                RestoreSceneSwap(swap);
            _sceneSwaps.Clear();
            _dialogHooks.Clear();
            ApplyRegistrations(rebuildSelections: false);
            HookSceneEvents();
            RefreshAppliedMusicMembers();
            return true;
        }

        // ---------- 场景钩子 ----------

        private void HookSceneEvents()
        {
            if (_hooked)
                return;
            _tree = Engine.GetMainLoop() as SceneTree;
            if (_tree == null)
                return;
            _tree.NodeAdded += OnNodeAdded;
            _tree.ProcessFrame += OnResumeFrame;
            _hooked = true;
            Callable.From(EnsurePlayerMounted).CallDeferred();
            TrackExistingScreens(_tree.Root);
        }

        private void TrackExistingScreens(Node node)
        {
            OnNodeAdded(node);
            foreach (Node child in node.GetChildren())
                TrackExistingScreens(child);
        }

        private void RestoreActiveDialogs()
        {
            foreach (DialogHook hook in _dialogHooks)
                if (_screenExits.Keys.Any(node => GodotObject.IsInstanceValid(node) && MatchesScreen(node, hook.Screen)))
                    RestoreDialogMusic(hook);
        }

        private void UnhookSceneEvents()
        {
            foreach (var pair in _playerDialogs)
                if (GodotObject.IsInstanceValid(pair.Key)) pair.Key.TreeExiting -= pair.Value;
            _playerDialogs.Clear();
            foreach (var pair in _screenExits)
                if (GodotObject.IsInstanceValid(pair.Key))
                    pair.Key.TreeExiting -= pair.Value;
            _screenExits.Clear();
            if (_hooked && GodotObject.IsInstanceValid(_tree))
            {
                _tree.NodeAdded -= OnNodeAdded;
                _tree.ProcessFrame -= OnResumeFrame;
            }
            _resumeSeen.Clear();
            _hooked = false;
            _tree = null;
        }

        private void OnResumeFrame()
        {
            if (!_running || _resumeWatchFailed || _appliedMusicKeys.Count == 0)
                return;
            AudioManager audio = AudioManager.Instance;
            if (audio == null
                || !audio.audioMemberDictionary.TryGetValue(AudioManagerEnum.TYPE.MUSIC, out Dictionary<string, AudioStreamPlayerMember> players))
                return;
            try
            {
                foreach (string key in _appliedMusicKeys)
                {
                    if ((_menuPlayer?.OwnsPlayback == true && _menuPlayer.OwnedKey == key)
                        || !ResumeMusicKeys.Contains(key)
                        || !players.TryGetValue(key, out AudioStreamPlayerMember player)
                        || !GodotObject.IsInstanceValid(player) || !player.IsPlaying())
                    {
                        _resumeSeen.Remove(key);
                        continue;
                    }
                    AudioStream stream = player.Stream;
                    float position = player.GetPlaybackPosition();
                    if (stream != null
                        && _resumeSeen.TryGetValue(key, out (AudioStream Stream, float Position) seen)
                        && ReferenceEquals(seen.Stream, stream)
                        && position < ResumeRestartedBelow
                        && seen.Position > ResumeHeardFrom
                        && stream.GetLength() - seen.Position > ResumeLoopMargin)
                    {
                        player.Play(seen.Position);
                        _resumeSeen[key] = (stream, seen.Position);
                        continue;
                    }
                    _resumeSeen[key] = (stream, position);
                }
            }
            catch (Exception exception)
            {
                // 读位置或接回失败（引擎版本不含该 API 等）就整块停用，绝不每帧刷错误。
                _resumeWatchFailed = true;
                _resumeSeen.Clear();
                _context?.Warn("界面音乐续播已停用：" + exception.GetBaseException().Message);
            }
        }

        private void OnNodeAdded(Node node)
        {
            if (_running && node != null) TrackPlayerDialog(node);
            // Ordinary option/help dialogs do not release or restart music. Explicit
            // Shop/Almanac handoffs retain their existing independent selections.
            if (node is Shop || node is Almanac)
                _menuPlayer?.Suspend();
            if (!_running || node == null || _screenExits.ContainsKey(node)
                || !ScreenSettings.Any(pair => MatchesScreen(node, pair.Screen)))
                return;
            Action exit = () => OnScreenExiting(node);
            _screenExits.Add(node, exit);
            node.TreeExiting += exit;

            foreach (SceneSwap swap in _sceneSwaps)
            {
                if (!MatchesScreen(node, swap.Screen))
                    continue;
                ApplySceneSwap(swap);
            }

            foreach (DialogHook hook in _dialogHooks)
            {
                if (!MatchesScreen(node, hook.Screen))
                    continue;
                StartDialogMusic(hook);
            }
        }

        private void OnScreenExiting(Node node)
        {
            if (_screenExits.TryGetValue(node, out Action exit))
                node.TreeExiting -= exit;
            _screenExits.Remove(node);
            if (!_running)
                return;
            foreach (SceneSwap swap in _sceneSwaps)
                if (MatchesScreen(node, swap.Screen)
                    && !_screenExits.Keys.Any(other => GodotObject.IsInstanceValid(other) && MatchesScreen(other, swap.Screen)))
                    RestoreSceneSwap(swap);
            foreach (DialogHook hook in _dialogHooks)
                if (MatchesScreen(node, hook.Screen)
                    && !_screenExits.Keys.Any(other => GodotObject.IsInstanceValid(other) && MatchesScreen(other, hook.Screen)))
                    RestoreDialogMusic(hook);
        }

        private static bool MatchesScreen(Node node, string screen) => screen switch
        {
            "LevelChoose" => node is LevelChoose,
            "LevelEditorStage" => node is LevelEditorStage,
            "AwardSettlement" => node is AwardSettlement,
            "Shop" => node is Shop,
            "MainMenu" => node is MainMenu,
            "Almanac" => node is Almanac,
            _ => false
        };

        private void ApplySceneSwap(SceneSwap swap)
        {
            var manager = ResourceManager.Instance;
            if (manager == null || swap.Applied)
                return;
            swap.HasOriginal = manager.AUDIOS.TryGetValue(swap.GameKey, out Resource original);
            swap.Original = original;
            manager.AUDIOS[swap.GameKey] = swap.Stream;
            swap.Applied = true;
            RefreshMusicMember(swap.GameKey);
        }

        private void RestoreSceneSwap(SceneSwap swap)
        {
            var manager = ResourceManager.Instance;
            if (manager == null || !swap.Applied)
                return;
            if (manager.AUDIOS.TryGetValue(swap.GameKey, out Resource current) && ReferenceEquals(current, swap.Stream))
            {
                if (_menuPlayer?.OwnedKey == swap.GameKey) _menuPlayer.Suspend();
                if (swap.HasOriginal)
                    manager.AUDIOS[swap.GameKey] = swap.Original;
                else
                    manager.AUDIOS.Remove(swap.GameKey);
                if (!RefreshMusicMember(swap.GameKey)) StopMusicMember(swap.GameKey);
            }
            swap.Applied = false;
        }

        private void StartDialogMusic(DialogHook hook)
        {
            var audio = AudioManager.Instance;
            if (audio == null)
                return;
            try
            {
                AudioStreamPlayerMember stop = audio.MemberFind(hook.StopKey, AudioManagerEnum.TYPE.MUSIC);
                if (GodotObject.IsInstanceValid(stop))
                    stop.Stop();
                audio.AudioPlay(hook.PlayKey, AudioManagerEnum.TYPE.MUSIC);
            }
            catch (Exception exception)
            {
                _context?.Warn($"切换界面音乐失败 {hook.Screen}：{exception.GetBaseException().Message}");
            }
        }

        private void RestoreDialogMusic(DialogHook hook)
        {
            var audio = AudioManager.Instance;
            if (audio == null || string.IsNullOrEmpty(hook.RestoreKey))
                return;
            try
            {
                AudioStreamPlayerMember mine = audio.MemberFind(hook.PlayKey, AudioManagerEnum.TYPE.MUSIC);
                if (GodotObject.IsInstanceValid(mine))
                    mine.Stop();
                audio.AudioPlay(hook.RestoreKey, AudioManagerEnum.TYPE.MUSIC);
            }
            catch (Exception exception)
            {
                _context?.Warn($"恢复界面音乐失败 {hook.Screen}：{exception.GetBaseException().Message}");
            }
        }

        // ---------- 让面板跟着目录和更新记录走 ----------

        // 原生「Mod 配置」的下拉与只读展示项都只认磁盘上 .pmod 里写的声明：面板每次打开都会
        // FindPlayerSettingsPackage → ScanMods 重新解析包体（XWModToolsPanel.Settings.cs:48-54），
        // 改内存里的清单宿主看不见。所以这里把两样东西写回自己安装包：
        //   ① 四个界面曲目的 options（目录里实际扫到的文件名）；
        //   ② 「关于」页 aboutChangelog 的正文（包内 changelog.txt 去掉标题行后的全部正文，
        //      这样更新记录只维护 changelog.txt 一处，展示项自动跟上）；
        //   ③ 「关于 → 检测更新渠道=关闭」时把 postId 清空、另两档还原成仓库里绑定的投稿 ID。
        //      游戏的官网检查只扫「包里有合法 postId」的 Mod，这是模组唯一能退出那次请求的办法；
        //      改的是包体，游戏这次读的还是内存里的旧清单，所以效果从下一次启动起生效。
        // 代价：包体哈希变了，本局 Mod 管理会标成「正在使用旧版本，重启后更新」，点「重启并应用」即恢复。
        private void SyncPackageDeclarations(string reason)
        {
            if (_context == null || _context.IsAndroid)
                return;

            List<string> tracks = _slots.Keys
                .OrderBy(stem => stem, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string package = FindOwnPackage();
            if (package == null)
                return;

            string originalHash = PackageHash(package);
            if (_packageHash != null && _packageHash != originalHash)
            {
                WarnOnce("安装包已更换", "检测到安装包已被外部更新，停止回写声明，请重启游戏。");
                return;
            }
            _packageHash = originalHash;
            string changelog = ChangelogBody(ReadPackageEntry(package, ChangelogEntryName));
            string postId = DesiredPostId();
            var signatureParts = new JsonArray();
            foreach (string stem in tracks)
                signatureParts.Add(new JsonObject { ["value"] = stem, ["label"] = TrackLabel(stem) });
            string signature = signatureParts.ToJsonString() + "#" + changelog + "#" + postId;
            if (signature == _declaredSignature)
                return;

            string temp = package + ".decl.tmp";
            try
            {
                string manifest = ReadPackageEntry(package, ManifestEntryName);
                if (manifest == null)
                {
                    _context.Warn("安装包里没有 mod.json，跳过面板声明同步。");
                    return;
                }
                switch (TryBuildManifestUpdate(manifest, tracks, changelog, postId, out string updated, out string error))
                {
                    case ManifestUpdate.Matched:
                        _declaredSignature = signature;
                        return;
                    case ManifestUpdate.Failed:
                        _context.Warn($"面板声明没更新：{error}");
                        return;
                }

                RepackPackage(package, temp, updated);
                if (!ModLoader.TryReadPackageMetadata(temp, out XWModManifest check, out _, out string diagnostic)
                    || !string.Equals(check?.Id, _context.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    _context.Warn("重写出来的安装包没通过宿主清单校验，保留原包：" + diagnostic);
                    return;
                }
                if (PackageHash(package) != originalHash)
                {
                    WarnOnce("安装包已更换", "同步期间安装包已被更新，保留新包并跳过回写。");
                    return;
                }
                string rewrittenHash = PackageHash(temp);
                File.Move(temp, package, overwrite: true);
                _packageHash = rewrittenHash;
                _declaredSignature = signature;
                _context.Log($"配置面板声明已同步（曲目={tracks.Count}）；本局可能显示旧版本，重启后恢复。");
            }
            catch (Exception exception)
            {
                _context.Warn("同步面板声明失败：" + exception.GetBaseException().Message);
            }
            finally
            {
                TryDeleteTemp(temp);
            }
        }

        // changelog.txt 首行是标题，「关于」页要展示的就是它下面的全部正文（整份更新记录，不是只取最新一条）。
        private static string ChangelogBody(string changelog)
        {
            if (string.IsNullOrEmpty(changelog))
                return "";
            string[] lines = changelog.Replace("\r\n", "\n").Split('\n');
            int end = lines.Length;
            while (end > 1 && string.IsNullOrWhiteSpace(lines[end - 1]))
                end--;
            var body = new List<string>();
            for (int index = 1; index < end; index++)
                body.Add(lines[index].TrimEnd());
            return string.Join("\n", body).Trim('\n');
        }

        private enum ManifestUpdate { Matched, Changed, Failed }

        // 只动四个界面曲目项的 type/options/default，以及 aboutChangelog 的 content，其余字段原样保留。
        private ManifestUpdate TryBuildManifestUpdate(string manifest, List<string> tracks, string changelog,
            string postId, out string updated, out string error)
        {
            updated = null;
            error = null;
            try
            {
                JsonNode root = JsonNode.Parse(manifest);
                bool changed = false;
                // 渠道=关闭时这里交进来的是空串：官网检查只扫得到带 postId 的包，清空就是退出那次检查。
                if (postId != null && TextOf(root?["postId"]) != postId)
                {
                    root["postId"] = postId;
                    changed = true;
                }
                JsonNode settings = root?["settings"];
                if (settings == null)
                {
                    error = "包内 mod.json 没有 settings 数组。";
                    return ManifestUpdate.Failed;
                }
                foreach (JsonNode item in settings.AsArray())
                {
                    string key = TextOf(item?["key"]);
                    if (key == null)
                        continue;
                    if (string.Equals(key, ChangelogSetting, StringComparison.Ordinal))
                    {
                        if (string.IsNullOrEmpty(changelog) || TextOf(item["type"]) != "text"
                            || TextOf(item["content"]) == changelog)
                            continue;
                        item["content"] = changelog;
                        changed = true;
                        continue;
                    }
                    if (!ScreenSettings.Any(pair => string.Equals(pair.Setting, key, StringComparison.Ordinal)))
                        continue;
                    JsonArray options = BuildTrackOptions(tracks);
                    string currentDefault = TextOf(item["default"]);
                    bool sameOptions = SameOptionValues(item["options"] as JsonArray, options);
                    bool sameType = TextOf(item["type"]) == "enum";
                    bool usableDefault = currentDefault != null
                        && options.Any(node => string.Equals(TextOf(node?["value"]), currentDefault, StringComparison.Ordinal));
                    if (sameOptions && sameType && usableDefault)
                        continue;
                    item["type"] = "enum";
                    item["options"] = options;
                    if (!usableDefault)
                        item["default"] = OffOption;
                    changed = true;
                }
                if (!changed)
                    return ManifestUpdate.Matched;
                updated = root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                return ManifestUpdate.Changed;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return ManifestUpdate.Failed;
            }
        }

        private JsonArray BuildTrackOptions(List<string> tracks)
        {
            var options = new JsonArray();
            options.Add(new JsonObject { ["value"] = OffOption, ["label"] = "原版（不替换）" });
            foreach (string stem in tracks)
                options.Add(new JsonObject { ["value"] = stem, ["label"] = TrackLabel(stem) });
            return options;
        }

        // 子文件夹里的曲目在标签上带出所在目录，值仍只是文件名（面板按值存取）。
        private string TrackLabel(string stem)
        {
            if (!_slots.TryGetValue(stem, out string file))
                return stem;
            string parent = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(parent))
                return stem;
            string relative = Path.GetRelativePath(ProjectSettings.GlobalizePath(BgmRootVirtualPath), parent);
            return string.IsNullOrEmpty(relative) || relative == "."
                ? stem
                : $"{stem}（{relative.Replace('\\', '/')}）";
        }

        private static bool SameOptionValues(JsonArray current, JsonArray desired)
        {
            if (current == null || current.Count != desired.Count)
                return false;
            for (int index = 0; index < current.Count; index++)
                if (!string.Equals(TextOf(current[index]?["value"]), TextOf(desired[index]?["value"]), StringComparison.Ordinal)
                    || !string.Equals(TextOf(current[index]?["label"]), TextOf(desired[index]?["label"]), StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static string TextOf(JsonNode node) =>
            node != null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

        private string FindOwnPackage()
        {
            string dir = ProjectSettings.GlobalizePath(ModsVirtualPath);
            if (!Directory.Exists(dir))
                return null;
            string found = null;
            foreach (string candidate in Directory.EnumerateFiles(dir, "*.pmod", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string id = null;
                try
                {
                    if (ModLoader.TryReadPackageMetadata(candidate, out XWModManifest manifest, out _, out _))
                    {
                        id = manifest?.Id;
                        if (string.Equals(id, _context.ModId, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(manifest?.Version, _context.ModVersion, StringComparison.Ordinal))
                        {
                            WarnOnce("安装包版本已变", "磁盘安装包版本与运行版本不同，跳过声明回写，请重启游戏。");
                            return null;
                        }
                    }
                }
                catch { continue; }
                if (!string.Equals(id, _context.ModId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found != null)
                {
                    _context.Warn($"Mods 目录里有两份以上 ID 为 {_context.ModId} 的安装包，面板下拉不会跟着目录更新。");
                    return null;
                }
                found = candidate;
            }
            if (found == null)
                _context.Warn($"{dir} 里没找到 ID 为 {_context.ModId} 的安装包，面板下拉不会跟着目录更新。");
            return found;
        }

        private static string PackageHash(string package) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package)));

        private string ReadPackageEntry(string package, string name)
        {
            try
            {
                using var archive = ZipFile.OpenRead(package);
                ZipArchiveEntry entry = archive.GetEntry(name);
                if (entry == null)
                    return null;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception exception)
            {
                _context.Warn("读安装包里的 " + name + " 失败：" + exception.GetBaseException().Message);
                return null;
            }
        }

        // 除 mod.json 外所有条目按原字节重打包；临时文件放同目录，换名一步完成。
        // CreateEntry 只用单参重载：带 CompressionLevel 的那个在游戏的裁剪运行时里不存在（实机 Method not found），
        // 宿主自己写包（ModExporter）也只调单参。单参的默认级别本来就是 Optimal，压缩结果不变。
        private static void RepackPackage(string package, string temp, string manifest)
        {
            TryDeleteTemp(temp);
            using var output = ZipFile.Open(temp, ZipArchiveMode.Create);
            using var input = ZipFile.OpenRead(package);
            ZipArchiveEntry manifestEntry = output.CreateEntry(ManifestEntryName);
            byte[] bytes = Encoding.UTF8.GetBytes(manifest);
            using (Stream stream = manifestEntry.Open())
                stream.Write(bytes, 0, bytes.Length);
            foreach (ZipArchiveEntry source in input.Entries)
            {
                if (source.FullName.Length == 0
                    || string.Equals(source.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                    continue;
                ZipArchiveEntry copy = output.CreateEntry(source.FullName);
                using Stream from = source.Open();
                using Stream to = copy.Open();
                from.CopyTo(to);
            }
        }

        private static void TryDeleteTemp(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 残留的临时文件不带 .pmod 扩展名，宿主不会当模组扫到，下次同名的时候会先清掉。
            }
        }

        // ---------- 工具方法 ----------

        // 玩家往目录里丢文件或换掉同名文件后不必重启：定时核对里比一下 BGM 目录的指纹。
        private void MaybeRescanMusicLibrary()
        {
            string stamp = MusicStamp();
            if (stamp == _musicStamp)
            {
                _pendingMusicStamp = null;
                return;
            }
            // 连续两次一致再读，避免文件刚开始复制就解码、重写包。
            if (stamp != _pendingMusicStamp)
            {
                _pendingMusicStamp = stamp;
                return;
            }
            if (!Reload(rescanLibrary: true))
                return;
            _pendingMusicStamp = null;
            _context?.Log($"音乐目录已更新：曲目={_slots.Count}，战斗覆盖={_bgmOverrides.Count}");
            SyncPackageDeclarations("目录变动");
        }
    }
}
