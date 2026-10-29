using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private readonly Dictionary<Node, Action> _playerDialogs = new();
        private void TrackPlayerDialog(Node node)
        {
            if (node is not DialogBoxBase || _playerDialogs.ContainsKey(node)) return;
            Action exit = null;
            exit = () => { node.TreeExiting -= exit; _playerDialogs.Remove(node); };
            _playerDialogs[node] = exit; node.TreeExiting += exit;
        }
        private bool BlockingPlayerDialog(string screen)
        {
            if (!(DialogManager.Instance?.HasOpenDialogs ?? false)) return false;
            if (screen != "Shop" && screen != "Almanac") return true;
            foreach (Node node in _playerDialogs.Keys)
                if (GodotObject.IsInstanceValid(node) && node.IsInsideTree() && !node.IsQueuedForDeletion()
                    && node is Control control && control.IsVisibleInTree()
                    && !(screen == "Shop" && node is Shop) && !(screen == "Almanac" && node is Almanac)) return true;
            return false;
        }
        private XWModLifetime _playerScope;
        private Control _playerMount;
        private sealed class PlayerContext
        {
            public string Screen, Setting, Key, Label;
            public PlayerContext(string screen, string setting, string key, string label)
            { Screen = screen; Setting = setting; Key = key; Label = label; }
        }
        private PlayerContext CurrentPlayerContext()
        {
            if (!_running || SceneManager.Instance == null || SceneManager.Instance.isLoading) return null;
            switch (SceneManager.CurrentScene)
            {
                case "MainMenu":
                    bool shop = false, almanac = false;
                    foreach (Node node in _screenExits.Keys)
                        if (GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion() && node.IsInsideTree()
                            && node is Control control && control.IsVisibleInTree())
                        { if (node is Shop) shop = true; if (node is Almanac) almanac = true; }
                    if (almanac) return new PlayerContext("Almanac", "menuAlmanac", "ChooseYourSeeds", "图鉴");
                    if (shop)
                    {
                        string key = "MainMenu";
                        foreach (DialogHook hook in _dialogHooks) if (hook.Screen == "Shop") key = hook.PlayKey;
                        return new PlayerContext("Shop", "menuShop", key, "商店");
                    }
                    return new PlayerContext("MainMenu", "menuMainMenu", "MainMenu", "主菜单");
                case "LevelChoose": return new PlayerContext("LevelChoose", "menuLevelChoose", "ZenGarden", "选关");
                case "LevelEditorStage": return new PlayerContext("LevelEditorStage", "menuLevelEditorStage", "ZenGarden", "自制关卡");
                case "AwardSettlement": return new PlayerContext("AwardSettlement", "menuAwardSettlement", "ZenGarden", "结算");
                // Selection cards are already TowerDefense. Sandbox also hosts a battle viewport.
                // Loading and unknown/custom scenes fail closed instead of leaking a menu player.
                default: return null;
            }
        }
        private void EnsurePlayerMounted()
        {
            if (!_running || _playerScope == null || _playerMount != null || _tree == null) return;
            _playerMount = _playerScope.TrackNode(new Control { Name = "MusicBoxViewportMount", MouseFilter = Control.MouseFilterEnum.Ignore });
            _tree.Root.AddChild(_playerMount);
            _playerMount.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var mounted = new MenuPlayer(this, new XWModUiSurface(_playerMount, _playerScope, "non-combat"));
            _menuPlayer = mounted.IsDisposed ? null : mounted;
            if (_menuPlayer != null) _playerScope.Track(_menuPlayer.Dispose);
            _context.Log("非战斗场景播放器已挂载。");
        }
    }
}
