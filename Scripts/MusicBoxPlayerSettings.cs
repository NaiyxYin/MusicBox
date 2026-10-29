using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PVZHE.ModEditor.ModSystem;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private bool MenuPlayerVisible()
        {
            if (_panelBool.TryGetValue(PlayerVisibleSetting, out bool visible)) return visible;
            try { return _settings?.Get<bool>(PlayerVisibleSetting) ?? true; }
            catch (Exception exception)
            { WarnOnce("播放器显示配置", "读取播放器显示开关失败，按显示处理：" + exception.GetBaseException().Message); return true; }
        }

        private bool PlayerAlwaysOnTop()
        {
            if (_panelBool.TryGetValue("playerAlwaysOnTop", out bool value)) return value;
            try { return _settings?.Get<bool>("playerAlwaysOnTop") ?? false; }
            catch (Exception exception)
            { WarnOnce("播放器置顶配置", "读取置顶开关失败：" + exception.GetBaseException().Message); return false; }
        }

        private string ConfiguredMenuTrack(string setting = "menuMainMenu") => setting != null && ReadSwitch(setting) ? ReadOption(setting) ?? OffOption : OffOption;

        private bool TrySaveMenuTrack(string track, out string message, string settingKey = "menuMainMenu")
        {
            message = "";
            if (!_running || _settings == null) { message = "原生 Mod 配置尚未激活。"; return false; }
            if (!ScreenSettings.Any(pair => pair.Setting == settingKey)) { message = "当前场景没有可保存的音乐配置。"; return false; }
            track = track?.Trim();
            if (string.IsNullOrEmpty(track)) { message = "曲目无效。"; return false; }
            try
            {
                // Decode before committing: a broken file must not replace the saved choice.
                if (track != OffOption && (!_slots.TryGetValue(track, out string file) || LoadAudio(file, true) == null))
                { message = "曲目不存在或无法解码，原配置保持不变。"; return false; }
                // Native enum definitions freeze at load. Open a public editor using the current
                // package declaration, NOT private mutation of the running settings instance.
                SyncPackageDeclarations("播放器选曲");
                string package = FindOwnPackage();
                if (package == null) { message = "找不到当前版本安装包，请重启后再试。"; return false; }
                string hash = PackageHash(package);
                if (!ModLoader.TryReadPackageMetadata(package, out XWModManifest manifest, out _, out string diagnostic))
                { message = "安装包配置读取失败：" + diagnostic; return false; }
                var setting = manifest.Settings?.FirstOrDefault(item => item.Key == settingKey);
                if (setting?.Options == null || !setting.Options.Any(option => option.Value == track))
                { message = "曲目声明尚未同步，未保存；请重启游戏后再试。"; return false; }
                if (PackageHash(package) != hash) { message = "安装包刚被修改，请重试。"; return false; }
                using var editor = XWModSettingsService.OpenEditor(manifest, hash);
                if (!string.IsNullOrEmpty(editor.Diagnostic))
                { message = "现有配置无法安全读取，未覆盖：" + editor.Diagnostic; return false; }
                var changes = new Dictionary<string, object>(StringComparer.Ordinal) { [settingKey] = track };
                // Selecting a real song opts into the existing menu group; restoring vanilla
                // changes ONLY menuMainMenu, leaving the Shop selection and group switch alone.
                if (track != OffOption && GroupSwitches.TryGetValue(settingKey, out string group)) changes[group] = true;
                if (!editor.TrySave(changes, out XWModSettingsSaveResult result))
                { message = result.Message; return false; }
            }
            catch (Exception exception)
            { message = exception.GetBaseException().Message; return false; }

            // A changed package hash may prevent the host from applying an immediate value.
            // Reuse our existing disk-compatible reload rather than treating the save as temporary.
            try
            {
                _panelStamp = DateTime.MinValue;
                if (!RefreshStoredValues()) throw new IOException("刚保存的配置暂时不可读取。");
                if (PanelSignature() != _appliedSignature && !Reload())
                    throw new IOException("当前播放未能应用新配置。");
                message = track == OffOption ? "已保存：当前界面使用原版音乐" : "已保存当前界面音乐：" + track;
            }
            catch (Exception exception)
            {
                message = "配置已保存，但即时应用失败，请重启：" + exception.GetBaseException().Message;
                _context.Warn(message);
            }
            return true;
        }
    }
}
