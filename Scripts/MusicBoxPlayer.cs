using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private MenuPlayer _menuPlayer;
        private XWModDataKey<string> _playerLayoutKey;
        private string _layoutSession;
        private string _layoutBinary;
        private long _layoutSequence;
        private float _playerRight = 24, _playerBottom = 96;
        private float _playerDefaultRight = 24, _playerDefaultBottom = 96;

        private void InitializeMenuPlayer()
        {
            _layoutSession = Guid.NewGuid().ToString("N");
            _layoutSequence = 0;
            using (var binary = File.OpenRead(Path.Combine(_context.PackageRoot, "Runtime", "ModAssembly.dll")))
                _layoutBinary = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary)).ToLowerInvariant();
            _playerLayoutKey = _context.Data.Declare("ui.player.layout", 1, new XWModCodec<string>(
                Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes.Span), value => value != null && value.Length < 4096));
            string defaults = Path.Combine(_context.PackageRoot, "Assets", "ui-layout.json");
            if (File.Exists(defaults)) ReadPlayerLayout(File.ReadAllText(defaults), true);
            if (_context.Data.TryRead(_playerLayoutKey, XWModDataScope.Local, out string saved, out string diagnostic))
                ReadPlayerLayout(saved, false);
            else if (!string.IsNullOrEmpty(diagnostic)) _context.Warn("播放器布局读取失败：" + diagnostic);
            _context.Log("UI_LAYOUT_READY: " + new JsonObject { ["schemaVersion"] = 1, ["modId"] = _context.ModId,
                ["sessionId"] = _layoutSession, ["binarySha256"] = _layoutBinary }.ToJsonString());
            _playerScope = _context.Lifetime.CreateChildScope();
        }

        private void ReadPlayerLayout(string text, bool defaults)
        {
            try
            {
                JsonNode root = JsonNode.Parse(text);
                JsonNode slot = defaults ? root?["placements"]?["main-menu/record/player"] : root;
                if (slot?["anchorX"]?.GetValue<string>() != "right" || slot?["anchorY"]?.GetValue<string>() != "bottom") return;
                float right = slot["rightInset"].GetValue<float>(), bottom = slot["verticalInset"].GetValue<float>();
                if (!float.IsFinite(right) || !float.IsFinite(bottom) || Math.Abs(right) > 100000 || Math.Abs(bottom) > 100000) return;
                if (defaults) { _playerDefaultRight = right; _playerDefaultBottom = bottom; }
                _playerRight = right; _playerBottom = bottom;
            }
            catch (Exception e) { _context.Warn("播放器布局无效，使用默认位置：" + e.GetBaseException().Message); }
        }

        private void SavePlayerLayout(Vector2 parent, Vector2 size, string reason)
        {
            var slot = new JsonObject { ["anchorX"] = "right", ["anchorY"] = "bottom",
                ["rightInset"] = _playerRight, ["verticalInset"] = _playerBottom };
            bool persisted = _context.Data.TryWrite(_playerLayoutKey, XWModDataScope.Local, slot.ToJsonString(), out string diagnostic);
            if (!persisted) _context.Warn("播放器位置保存失败：" + diagnostic);
            slot["schemaVersion"] = 1; slot["modId"] = _context.ModId; slot["componentId"] = "player";
            slot["sceneId"] = "main-menu"; slot["layoutId"] = "record"; slot["sessionId"] = _layoutSession;
            slot["binarySha256"] = _layoutBinary; slot["sequence"] = ++_layoutSequence;
            slot["coordinateSpace"] = "parent-logical-pixels"; slot["reason"] = reason; slot["persisted"] = persisted;
            slot["parentSize"] = new JsonArray(parent.X, parent.Y); slot["size"] = new JsonArray(size.X, size.Y);
            _context.Log("UI_LAYOUT_CHANGED: " + slot.ToJsonString());
        }

        // Exported hosts trim unused BCL overloads: avoid Array.FindIndex<T>(..., Predicate<T>).
        private static int FindTrackIndex(string[] names, string selected)
        {
            if (names == null) return -1;
            for (int index = 0; index < names.Length; index++)
                if (names[index] == selected) return index;
            return -1;
        }

        // Native nodes and signals keep this loaded Mod independent of script source generators.
        private sealed partial class MenuPlayer : IDisposable
        {
            private const float Diameter = 124;
            private static readonly Vector2 OpenSize = new(336, 264);
            private readonly MusicBoxEntry owner;
            private readonly CanvasLayer layer;
            private readonly Control parent, root, ring, details;
            private readonly TextureRect record;
            private readonly TextureButton disc;
            private readonly StyleBoxFlat bodyStyle;
            private readonly Label title, time, status, state, hint;
            private readonly OptionButton tracks;
            private readonly HSlider seek, volume;
            private readonly Button toggle, miniPrevious, miniNext, miniToggle;
            private readonly SceneTree tree;
            private readonly Window window;
            private readonly ImageTexture texture;
            private ImageTexture hitTexture;
            private readonly Bitmap mask = new();
            private readonly PlayerDragGesture gesture = new();
            private string[] library = Array.Empty<string>();
            private string libraryStamp = "", choice;
            private PlayerContext context;
            private bool disposed, updating, expanded, seeking, active, blocked, visibleSetting = true;
            private float openProgress, angle, ratio, resumePosition, gain = 1;
            private bool paused;
            private ulong previousTick;
            private Vector2I maskSize;
            private int maskRadius;
            private AudioStreamPlayerMember player;
            private AudioStream expected;
            private Resource registry;
            private float originalVolume, appliedVolume;
            private bool originalPaused;
            private Node.ProcessModeEnum originalProcessMode;
            public string OwnedKey => context?.Key;
            public bool IsDisposed => disposed;
            public bool OwnsPlayback => GodotObject.IsInstanceValid(player) && ReferenceEquals(player.Stream, expected)
                && ResourceManager.Instance != null && ResourceManager.Instance.AUDIOS.TryGetValue(context?.Key ?? "", out Resource current)
                && ReferenceEquals(current, registry);

            public MenuPlayer(MusicBoxEntry owner, XWModUiSurface surface)
            {
                this.owner = owner;
                layer = surface.Lifetime.TrackNode(new CanvasLayer { Name = "MusicBoxPlayerLayer", Layer = 1, ProcessMode = Node.ProcessModeEnum.Always });
                surface.Root.AddChild(layer);
                parent = new Control { Name = "MusicBoxPlayerSurface", MouseFilter = Control.MouseFilterEnum.Ignore };
                layer.AddChild(parent); parent.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                root = new Control { Name = "MusicBoxRecord", Size = new Vector2(Diameter, Diameter), MouseFilter = Control.MouseFilterEnum.Ignore };
                parent.AddChild(root);
                bodyStyle = new StyleBoxFlat { BgColor = new Color("101c22"), BorderColor = new Color("34534d"),
                    BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
                    ShadowColor = new Color(0, 0, 0, .35f), ShadowSize = 8, ShadowOffset = new Vector2(0, 3) };
                root.Draw += DrawSurface;
                disc = new TextureButton { Name = "PlayerDragSurface", IgnoreTextureSize = true,
                    StretchMode = TextureButton.StretchModeEnum.Scale, Modulate = new Color(1, 1, 1, 0),
                    FocusMode = Control.FocusModeEnum.All };
                root.AddChild(disc); disc.GuiInput += DiscInput;
                using (var image = Image.CreateEmpty(104, 104, false, Image.Format.Rgba8))
                {
                    for (int y = 0; y < 104; y++) for (int x = 0; x < 104; x++)
                    {
                        float dx = x - 51.5f, dy = y - 51.5f, radius = Mathf.Sqrt(dx * dx + dy * dy);
                        Color color = new(0, 0, 0, 0);
                        if (radius < 49)
                        {
                            float groove = (int)(radius / 2) % 2 == 0 ? .07f : .11f;
                            color = radius < 19 ? new Color("459b7c") : new Color(groove, groove + .015f, groove + .025f);
                            if (radius > 21 && dx * .8f + dy * .6f > radius * .86f) color = color.Lightened(.13f);
                            if (radius < 3) color = new Color("16282b");
                        }
                        image.SetPixel(x, y, color);
                    }
                    texture = ImageTexture.CreateFromImage(image);
                }
                record = new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
                root.AddChild(record);
                ring = new Control { MouseFilter = Control.MouseFilterEnum.Ignore }; root.AddChild(ring); ring.Draw += DrawRing;
                hint = MakeLabel(root, "空白处展开", 10, new Vector2(12, 98), new Vector2(100, 18), "a7c7bb");
                hint.HorizontalAlignment = HorizontalAlignment.Center;
                miniPrevious = MakeButton(root, "‹", new Vector2(6, 44), new Vector2(30, 32), () => Step(-1), true);
                miniNext = MakeButton(root, "›", new Vector2(88, 44), new Vector2(30, 32), () => Step(1), true);
                miniToggle = MakeButton(root, "Ⅱ", new Vector2(46, 44), new Vector2(32, 32), TogglePause, true);
                miniToggle.AddThemeFontSizeOverride("font_size", 18);
                miniPrevious.AddThemeFontSizeOverride("font_size", 23); miniNext.AddThemeFontSizeOverride("font_size", 23);
                details = new Control { Name = "PlayerDetails", MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true, Visible = false };
                root.AddChild(details);
                MakeLabel(details, "MUSIC BOX  /  音乐盒", 12, new Vector2(16, 10), new Vector2(210, 24), "8fb8aa");
                MakeButton(details, "收起", new Vector2(264, 8), new Vector2(56, 28), () => SetExpanded(false));
                title = MakeLabel(details, "原版主菜单音乐", 17, new Vector2(110, 46), new Vector2(210, 28), "edf5f0");
                title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                state = MakeLabel(details, "正在播放", 11, new Vector2(16, 132), new Vector2(88, 20), "8fb8aa");
                seek = new HSlider { Position = new Vector2(110, 78), Size = new Vector2(210, 18), MinValue = 0, MaxValue = 1, Step = .01 };
                details.AddChild(seek); StyleSlider(seek);
                seek.DragStarted += () => seeking = true;
                seek.DragEnded += changed => { seeking = false; if (changed) SeekTo((float)seek.Value); };
                seek.ValueChanged += value => { if (!updating && !seeking) SeekTo((float)value); };
                time = MakeLabel(details, "00:00 / 00:00", 11, new Vector2(110, 99), new Vector2(210, 20), "97aaa8");
                var transport = new HBoxContainer { Name = "TransportRow", Position = new Vector2(110, 122), Size = new Vector2(210, 32) };
                transport.AddThemeConstantOverride("separation", 6); details.AddChild(transport);
                var previous = MakeButton(transport, "上一首", Vector2.Zero, new Vector2(60, 32), () => Step(-1));
                toggle = MakeButton(transport, "暂停", Vector2.Zero, new Vector2(78, 32), TogglePause, true);
                var next = MakeButton(transport, "下一首", Vector2.Zero, new Vector2(60, 32), () => Step(1));
                foreach (Button button in new[] { previous, toggle, next })
                { button.CustomMinimumSize = new Vector2(0, 32); button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; }
                tracks = new OptionButton { Name = "SavedTrack", Position = new Vector2(16, 166), Size = new Vector2(304, 32), FitToLongestItem = false };
                StyleButton(tracks, false); details.AddChild(tracks); tracks.ItemSelected += SelectTrack;
                MakeLabel(details, "音量", 12, new Vector2(16, 212), new Vector2(34, 22), "97aaa8");
                volume = new HSlider { Position = new Vector2(56, 210), Size = new Vector2(152, 24), MinValue = 0, MaxValue = 1, Step = .01, Value = 1 };
                details.AddChild(volume); StyleSlider(volume); volume.ValueChanged += value => { gain = (float)value; ApplyVolume(); };
                orderSelector = new OptionButton { Position = new Vector2(236, 208), Size = new Vector2(84, 28), FitToLongestItem = false };
                foreach (string label in new[] { "顺序", "倒序", "乱序", "循环" }) orderSelector.AddItem(label);
                StyleButton(orderSelector, false); details.AddChild(orderSelector); orderSelector.ItemSelected += SetOrder;
                status = MakeLabel(details, "切歌直接保存至当前界面配置", 11, new Vector2(16, 240), new Vector2(304, 18), "8fb8aa");
                status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                tree = parent.GetTree(); window = parent.GetWindow();
                parent.Resized += OnParentResized; window.FocusExited += CancelGesture;
                InstallPointerIsolation(root);
                tree.ProcessFrame += Tick; SceneManager.Instance.OnSceneChange += SceneChanging;
                previousTick = Time.GetTicksMsec(); Layout(); Tick();
            }

            private static Label MakeLabel(Control box, string text, int font, Vector2 position, Vector2 size, string color)
            {
                var label = new Label { Text = text, Position = position, Size = size, MouseFilter = Control.MouseFilterEnum.Ignore };
                label.AddThemeFontSizeOverride("font_size", font); label.AddThemeColorOverride("font_color", new Color(color)); box.AddChild(label); return label;
            }
            private static StyleBoxFlat ButtonStyle(string fill, string border, int radius = 10)
            {
                return new StyleBoxFlat { BgColor = new Color(fill), BorderColor = new Color(border),
                    BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
                    CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
                    ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 3, ContentMarginBottom = 3 };
            }
            private static void StyleButton(Button button, bool accent)
            {
                button.AddThemeStyleboxOverride("normal", ButtonStyle(accent ? "284f43" : "1b2c32", accent ? "3e7460" : "31484c"));
                button.AddThemeStyleboxOverride("hover", ButtonStyle("345e50", "6da68c"));
                button.AddThemeStyleboxOverride("pressed", ButtonStyle("153d32", "7fc8a7"));
                button.AddThemeStyleboxOverride("focus", ButtonStyle("00000000", "a2e5c6"));
                button.AddThemeFontSizeOverride("font_size", 13); button.AddThemeColorOverride("font_color", new Color("dfede6"));
                button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            }
            private static Button MakeButton(Control box, string text, Vector2 position, Vector2 size, Action action, bool accent = false)
            {
                var button = new Button { Text = text, Position = position, Size = size, MouseFilter = Control.MouseFilterEnum.Stop };
                StyleButton(button, accent); box.AddChild(button); button.Pressed += action; return button;
            }
            private static void StyleSlider(HSlider slider)
            {
                slider.AddThemeStyleboxOverride("slider", ButtonStyle("263e42", "263e42", 3));
                slider.AddThemeStyleboxOverride("grabber_area", ButtonStyle("71b99a", "71b99a", 3));
                slider.AddThemeStyleboxOverride("grabber_area_highlight", ButtonStyle("8ad4b4", "8ad4b4", 3));
                slider.MouseFilter = Control.MouseFilterEnum.Stop;
            }
            private bool Available() => !disposed && !transitioning && owner.CurrentPlayerContext() != null;
            private bool CanInteract() => Available() && visibleSetting
                && (!tree.Paused || context?.Screen == "Almanac" || context?.Screen == "Shop")
                && !owner.BlockingPlayerDialog(context?.Screen);
            private void SceneChanging(string target)
            {
                RememberPlayback();
                if (target != "MainMenu" && target != "LevelChoose" && target != "LevelEditorStage" && target != "AwardSettlement") resumePending = false;
                transitionSource = tree.CurrentScene ?? ReadySceneRoot(context?.Screen ?? SceneManager.CurrentScene);
                startRequested = false;
                transitioning = true; Suspend(); root.Hide();
            }
            public void Suspend() { CancelGesture(); Release(); active = false; }
            public void ResetPlayback()
            { Release(); choice = null; resumePosition = 0; paused = false; blocked = false; libraryStamp = ""; }
            public void RefreshVisibility()
            {
                bool show = owner.MenuPlayerVisible();
                if (!show)
                {
                    CancelGesture();
                    if (visibleSetting) SetExpanded(false, true);
                    root.Hide();
                }
                visibleSetting = show;
            }
            private void Tick()
            {
                if (disposed) return;
                try
                {
                    ulong now = Time.GetTicksMsec(); float delta = Math.Min((now - previousTick) / 1000f, .1f); previousTick = now;
                    if (!TransitionReady()) { root.Hide(); return; }
                    var nextContext = owner.CurrentPlayerContext();
                    if (nextContext == null) { if (active || gesture.Pressed) Suspend(); context = null; root.Hide(); return; }
                    if (context == null || context.Screen != nextContext.Screen || context.Key != nextContext.Key)
                    { ResetPlayback(); context = nextContext; active = false; }
                    if (!active) { active = true; blocked = false; Acquire(); }
                    if (player != null && !OwnsPlayback) RecoverAudioBinding();
                    if (!blocked && player == null) Acquire();
                    SyncOrderSelector();
                    // Visibility is presentation-only: retain the audio lease, volume and pause.
                    RefreshVisibility();
                    if (!visibleSetting) return;
                    root.Show(); RefreshLibrary();
                    bool interactive = CanInteract();
                    root.ProcessMode = interactive ? Node.ProcessModeEnum.Always : Node.ProcessModeEnum.Disabled;
                    var dialogCanvas = DialogManager.Instance?.GetNodeOrNull<CanvasLayer>("DialogLayer");
                    int modalLayer = dialogCanvas?.Layer ?? 100;
                    int wantedLayer = context.Screen == "MainMenu" ? 1 : 20;
                    if (owner.PlayerAlwaysOnTop()) wantedLayer = Math.Max(wantedLayer, modalLayer - 1);
                    if (context.Screen == "Shop" || context.Screen == "Almanac") wantedLayer = modalLayer + 1;
                    if (!interactive) wantedLayer = Math.Min(wantedLayer, modalLayer - 1);
                    if (layer.Layer != wantedLayer) layer.Layer = wantedLayer;
                    if (!interactive && gesture.Pressed) CancelGesture();
                    RefreshInputShield();
                    if (gesture.Pressed && (!Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(Key.Escape))) CancelGesture();
                    float target = expanded ? 1 : 0;
                    if (openProgress != target)
                    { openProgress = Mathf.MoveToward(openProgress, target, delta / .20f); Layout(); }
                    if (OwnsPlayback)
                    {
                        RememberPlayback();
                        float length = (float)(player.Stream?.GetLength() ?? 0), position = player.GetPlaybackPosition();
                        resumePosition = playlistEnded ? 0 : position; paused = player.StreamPaused;
                        if (playlistEnded) position = length;
                        bool playing = player.Playing && !paused;
                        if (playing) { angle += delta * .35f; record.Rotation = angle; }
                        ratio = length > 0 && float.IsFinite(length) ? Mathf.Clamp(position / length, 0, 1) : 0;
                        toggle.Text = playing ? "暂停" : "播放"; state.Text = playing ? "正在播放" : playlistEnded ? "播放完毕" : "已暂停";
                        miniToggle.Text = playing ? "Ⅱ" : "▶";
                        hint.Text = playing ? "空白处展开" : "已暂停 · 展开";
                        updating = true; seek.Editable = length > 0 && float.IsFinite(length) && openProgress >= 1;
                        if (seek.Editable) seek.MaxValue = length;
                        if (!seeking) seek.Value = position;
                        updating = false;
                        time.Text = FormatTime(position) + "   /   " + FormatTime(length);
                        title.Text = choice ?? (context.Label + " · 原版音乐");
                    }
                    else { seek.Editable = false; state.Text = "未在播放"; }
                    ring.QueueRedraw();
                }
                catch (Exception e) { owner.WarnOnce("播放器组件", "播放器组件已停用：" + e.GetBaseException().Message); Dispose(); }
            }
            private void RefreshLibrary()
            {
                string stamp = owner._musicStamp;
                if (libraryStamp != stamp || tracks.ItemCount == 0)
                {
                    libraryStamp = stamp; library = owner._slots.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                    tracks.Clear(); tracks.AddItem("原版（保存为不替换）"); foreach (string name in library) tracks.AddItem(name);
                }
                string configured = owner.ConfiguredMenuTrack(context?.Setting); choice = configured == OffOption ? null : configured;
                tracks.Select(FindTrackIndex(library, choice) + 1);
            }
            private void Acquire()
            {
                if (!Available() || blocked || context == null) return;
                var found = AudioManager.Instance?.MemberFind(context.Key, AudioManagerEnum.TYPE.MUSIC);
                if (!GodotObject.IsInstanceValid(found) || found.IsQueuedForDeletion() || found.Stream == null) return;
                if (!found.Playing && !found.StreamPaused && !startRequested) return;
                player = found; expected = player.Stream;
                ResourceManager.Instance.AUDIOS.TryGetValue(context.Key, out registry);
                originalPaused = player.StreamPaused; originalVolume = player.VolumeDb;
                originalProcessMode = player.ProcessMode;
                bool enginePaused = tree.Paused && originalProcessMode == Node.ProcessModeEnum.Pausable;
                paused = originalPaused && !enginePaused;
                player.ProcessMode = Node.ProcessModeEnum.Always;
                boundTrack = TrackIdentity();
                player.StreamPaused = paused; ApplyVolume(); AttachPlaylist(); RestoreBookmark();
                if (startRequested) { startRequested = false; playlistEnded = false; player.Play(0); player.StreamPaused = false; }
                status.Text = "切歌直接保存至当前界面配置";
            }
            private void ApplyVolume()
            {
                if (!OwnsPlayback) return;
                appliedVolume = gain <= 0 ? -80 : Mathf.Clamp(originalVolume + Mathf.LinearToDb(gain), -80, 24); player.VolumeDb = appliedVolume;
            }
            private void Release()
            {
                DetachPlaylist();
                if (OwnsPlayback)
                {
                    resumePosition = player.GetPlaybackPosition(); paused = player.StreamPaused;
                    if (Math.Abs(player.VolumeDb - appliedVolume) < .01f) player.VolumeDb = originalVolume;
                }
                RestoreProcessMode();
                player = null; expected = null; registry = null; owner._resumeSeen.Remove(context?.Key ?? "");
            }
            private void RestoreProcessMode()
            {
                if (GodotObject.IsInstanceValid(player) && player.ProcessMode == Node.ProcessModeEnum.Always)
                {
                    bool wasPaused = player.StreamPaused;
                    player.ProcessMode = originalProcessMode;
                    if (OwnsPlayback) player.StreamPaused = wasPaused;
                }
            }
            private void SelectTrack(long index)
            {
                if (updating || !CanInteract()) return;
                string next = index > 0 && index <= library.Length ? library[index - 1] : null;
                if (!owner.TrySaveMenuTrack(next ?? OffOption, out string message, context?.Setting))
                {
                    tracks.Select(FindTrackIndex(library, choice) + 1); status.Text = "未保存：" + message;
                    owner.WarnOnce("播放器保存 " + message, status.Text); return;
                }
                paused = false; blocked = false; startRequested = true; Tick();
                if (OwnsPlayback && !player.Playing) { playlistEnded = false; player.Play(0); player.StreamPaused = false; }
                status.Text = message;
            }
            private void Step(int direction)
            {
                if (library.Length == 0) { status.Text = "BGM 目录尚无可播放的曲目"; hint.Text = "暂无曲目"; return; }
                int index = FindTrackIndex(library, choice);
                int next = PlaylistNext(library.Length, index, owner.ReadPlaybackOrder(), direction);
                if (next < 0) { status.Text = "已到列表边界"; return; }
                tracks.Select(next + 1); SelectTrack(next + 1);
            }
            private void TogglePause()
            {
                if (!CanInteract()) return;
                if (blocked)
                {
                    ResetPlayback(); owner.Reload(); startRequested = true; Tick();
                    status.Text = OwnsPlayback ? "已重新同步当前界面音乐" : "正在等待界面音乐就绪";
                    return;
                }
                if (!OwnsPlayback) return;
                if (!player.Playing) { playlistEnded = false; player.Play(resumePosition); player.StreamPaused = false; } else player.StreamPaused = !player.StreamPaused;
                paused = player.StreamPaused;
            }
            private void SeekTo(float position)
            {
                if (!CanInteract() || !OwnsPlayback) return;
                float length = (float)player.Stream.GetLength(); if (length <= 0 || !float.IsFinite(length)) return;
                player.Seek(Mathf.Clamp(position, 0, Math.Max(0, length - .05f))); resumePosition = position; owner._resumeSeen.Remove(context?.Key ?? "");
            }
            private static string FormatTime(float seconds)
            { int total = float.IsFinite(seconds) ? (int)Math.Clamp(seconds, 0, 359999) : 0; return (total / 60).ToString("00") + ":" + (total % 60).ToString("00"); }

            private float Shape => 1 - Mathf.Pow(1 - openProgress, 3);
            private void DrawSurface()
            {
                int radius = (int)Mathf.Lerp(Diameter / 2, 22, Shape);
                bodyStyle.CornerRadiusTopLeft = bodyStyle.CornerRadiusTopRight = radius;
                bodyStyle.CornerRadiusBottomLeft = bodyStyle.CornerRadiusBottomRight = radius;
                bodyStyle.BorderColor = new Color(disc.HasFocus() ? "7da993" : "34534d");
                root.DrawStyleBox(bodyStyle, new Rect2(Vector2.Zero, root.Size));

            }
            private void DrawRing()
            {
                Vector2 center = new Vector2(Diameter / 2, Diameter / 2).Lerp(record.Position + record.Size / 2, Shape);
                float radius = Mathf.Lerp(Diameter / 2 - 6, record.Size.X / 2 + 3, Shape);
                ring.DrawArc(center, radius, 0, Mathf.Tau, 96, new Color("304a48"), 2, true);
                if (ratio > 0) ring.DrawArc(center, radius, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * ratio, 96, new Color("89d3ac"), 3, true);
            }
            private void UpdateHitMask()
            {
                var size = new Vector2I(Math.Max(1, (int)root.Size.X), Math.Max(1, (int)root.Size.Y));
                int radius = (int)Mathf.Lerp(Diameter / 2, 22, Shape);
                if (size == maskSize && radius == maskRadius) return;
                maskSize = size; maskRadius = radius;
                byte[] data = new byte[size.X * size.Y * 4];
                for (int y = 0; y < size.Y; y++) for (int x = 0; x < size.X; x++)
                {
                    float dx = Math.Max(0, Math.Max(radius - x - .5f, x + .5f - (size.X - radius)));
                    float dy = Math.Max(0, Math.Max(radius - y - .5f, y + .5f - (size.Y - radius)));
                    if (dx * dx + dy * dy <= radius * radius) data[(y * size.X + x) * 4 + 3] = 255;
                }
                using var image = Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, data);
                mask.CreateFromImageAlpha(image, .1f);
                var old = hitTexture; hitTexture = ImageTexture.CreateFromImage(image); disc.TextureNormal = hitTexture; disc.TextureClickMask = mask; old?.Dispose();
            }
            private void SetExpanded(bool value, bool instant = false)
            {
                CancelGesture(); expanded = value; if (instant) openProgress = value ? 1 : 0; Layout();
            }
            private void DiscInput(InputEvent input)
            {
                if (!CanInteract()) return;
                if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
                {
                    disc.AcceptEvent();
                    Vector2 pointer = root.Position + mouse.Position;
                    if (mouse.Pressed)
                    {
                        // Complete a shape transition before fixing the grab point, never tween a drag.
                        if (openProgress != (expanded ? 1 : 0)) { openProgress = expanded ? 1 : 0; Layout(); pointer = root.Position + mouse.Position; }
                        Vector2 tolerance = new(4, 4);
                        try { tolerance = NativeDragTolerance(parent, window); }
                        catch (Exception exception)
                        { owner.WarnOnce("拖动容差回退", "无法读取系统拖动容差，使用安全默认值：" + exception.GetBaseException().Message); }
                        gesture.Begin(pointer, root.Position, tolerance);
                    }
                    else if (gesture.Pressed)
                    {
                        if (gesture.Dragging)
                        {
                            gesture.Move(pointer, out Vector2 position); root.Position = ClampPosition(position);
                            CommitPosition(); gesture.End(); Layout();
                        }
                        else
                        {
                            bool inside = new Rect2(Vector2.Zero, root.Size).HasPoint(mouse.Position)
                                && mask.GetBitv(new Vector2I((int)mouse.Position.X, (int)mouse.Position.Y));
                            bool clicked = gesture.End(); if (clicked && inside) SetExpanded(!expanded);
                        }
                        disc.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                    }
                }
                else if (input is InputEventMouseMotion motion && gesture.Pressed)
                {
                    disc.AcceptEvent();
                    if ((motion.ButtonMask & MouseButtonMask.Left) == 0) { CancelGesture(); return; }
                    if (gesture.Move(root.Position + motion.Position, out Vector2 position))
                    { root.Position = ClampPosition(position); disc.MouseDefaultCursorShape = Control.CursorShape.Drag; }
                }
                else if (input is InputEventKey { Pressed: true, Keycode: Key.Escape })
                { CancelGesture(); SetExpanded(false); disc.AcceptEvent(); }
                else if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space or Key.Enter })
                { SetExpanded(!expanded); disc.AcceptEvent(); }
            }
            private void CommitPosition()
            {
                // Store the same right/bottom edge in both shapes; opening never changes intent.
                float right = parent.Size.X - root.Position.X - root.Size.X, bottom = parent.Size.Y - root.Position.Y - root.Size.Y;
                if (Math.Abs(right - owner._playerRight) <= .01f && Math.Abs(bottom - owner._playerBottom) <= .01f) return;
                owner._playerRight = right; owner._playerBottom = bottom;
                owner.SavePlayerLayout(parent.Size, root.Size, "left-drag");
            }
            private Vector2 ClampPosition(Vector2 p) => new(Math.Clamp(p.X, 0, Math.Max(0, parent.Size.X - root.Size.X)),
                Math.Clamp(p.Y, 0, Math.Max(0, parent.Size.Y - root.Size.Y)));
            private void CancelGesture()
            { gesture.Cancel(); ReleaseInputShield(); if (!disposed) { disc.MouseDefaultCursorShape = Control.CursorShape.PointingHand; Layout(); } }
            private void OnParentResized() { CancelGesture(); Layout(); }
            private void Layout()
            {
                if (disposed || gesture.Dragging) return;
                float shape = Shape;
                root.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
                root.Size = new Vector2(Diameter, Diameter).Lerp(OpenSize, shape).Min(parent.Size.Max(new Vector2(1, 1)));
                root.Position = ClampPosition(new Vector2(parent.Size.X - root.Size.X - owner._playerRight, parent.Size.Y - root.Size.Y - owner._playerBottom));
                disc.Size = root.Size; ring.Size = root.Size;
                Vector2 recordSize = new Vector2(60, 60).Lerp(new Vector2(80, 80), shape);
                record.Position = new Vector2(32, 24).Lerp(new Vector2(16, 48), shape); record.Size = recordSize; record.PivotOffset = recordSize / 2;
                details.Size = root.Size; details.Visible = openProgress > .75f; details.Modulate = new Color(1, 1, 1, Mathf.Clamp((openProgress - .75f) * 4, 0, 1));
                details.MouseFilter = Control.MouseFilterEnum.Ignore;
                volume.Editable = openProgress >= 1;
                volume.MouseFilter = seek.MouseFilter = openProgress >= 1 ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
                foreach (Node child in details.GetChildren())
                    if (child is BaseButton button) button.Disabled = openProgress < 1;
                miniPrevious.Visible = miniNext.Visible = miniToggle.Visible = hint.Visible = openProgress < .12f;
                miniPrevious.Disabled = miniNext.Disabled = miniToggle.Disabled = openProgress > 0;
                UpdateHitMask(); root.QueueRedraw(); ring.QueueRedraw();
            }
            public void Dispose()
            {
                if (disposed) return;
                ReleaseInputShield(); Release(); gesture.Cancel(); disposed = true;
                tree.ProcessFrame -= Tick;
                if (GodotObject.IsInstanceValid(window)) window.FocusExited -= CancelGesture;
                if (SceneManager.Instance != null) SceneManager.Instance.OnSceneChange -= SceneChanging;
                if (GodotObject.IsInstanceValid(parent)) parent.Resized -= OnParentResized;
                if (GodotObject.IsInstanceValid(layer)) layer.QueueFree();
                if (ReferenceEquals(owner._menuPlayer, this)) owner._menuPlayer = null;
            }
        }
    }
}

