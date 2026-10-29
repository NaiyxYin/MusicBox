using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private string ReadPlaybackOrder()
        {
            string mode = ReadOption("playbackOrder");
            return mode == "sequence" || mode == "reverse" || mode == "shuffle" ? mode : "loop";
        }
        private bool SavePlaybackOrder(string mode, out string message)
        {
            message = "";
            try
            {
                SyncPackageDeclarations("播放顺序");
                string package = FindOwnPackage();
                if (package == null) { message = "找不到当前版本安装包，请重启。"; return false; }
                string hash = PackageHash(package);
                if (!ModLoader.TryReadPackageMetadata(package, out XWModManifest manifest, out _, out message)) return false;
                using var editor = XWModSettingsService.OpenEditor(manifest, hash);
                if (editor.Diagnostic.Length != 0) { message = editor.Diagnostic; return false; }
                if (!editor.TrySave(new Dictionary<string, object> { ["playbackOrder"] = mode }, out var result))
                { message = result.Message; return false; }
                _panelStamp = DateTime.MinValue;
                RefreshStoredValues();
                return true;
            }
            catch (Exception ex) { message = ex.GetBaseException().Message; return false; }
        }
        private static int PlaylistNext(int count, int current, string mode, int direction)
        {
            if (count <= 0) return -1;
            if (mode == "shuffle")
            {
                if (count == 1) return 0;
                if (current < 0 || current >= count) return (int)(GD.Randi() % (uint)count);
                int next = (int)(GD.Randi() % (uint)(count - 1));
                return current >= 0 && next >= current ? next + 1 : next;
            }
            int step = mode == "reverse" ? -direction : direction;
            if (current < 0) return step > 0 ? 0 : count - 1;
            int index = current + step;
            if (index >= 0 && index < count) return index;
            return mode == "loop" ? (index + count) % count : -1;
        }
        private sealed partial class MenuPlayer
        {
            private readonly string[] orderModes = { "sequence", "reverse", "shuffle", "loop" };
            private OptionButton orderSelector;
            private AudioStream originalPlaylistStream, playlistStream;
            private long playlistLease;
            private bool playlistEnded;
            private void SetOrder(long index)
            {
                if (updating || !CanInteract() || index < 0 || index >= orderModes.Length) return;
                if (!owner.SavePlaybackOrder(orderModes[index], out string error)) status.Text = "顺序未保存：" + error;
                else status.Text = "播放顺序已保存";
                SyncOrderSelector();
            }
            private void SyncOrderSelector()
            {
                int index = FindTrackIndex(orderModes, owner.ReadPlaybackOrder());
                orderSelector.Select(index < 0 ? 3 : index);
            }
            private void AttachPlaylist()
            {
                playlistLease++; playlistEnded = false;
                // Never change the shared cached AudioStream or vanilla BGM looping flags.
                if (owner.ConfiguredMenuTrack(context.Setting) == OffOption) return;
                AudioStream copy = player.Stream.Duplicate() as AudioStream;
                if (copy is AudioStreamWav wav) wav.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
                else if (copy is AudioStreamOggVorbis ogg) ogg.Loop = false;
                else if (copy is AudioStreamMP3 mp3) mp3.Loop = false;
                else { copy?.Dispose(); return; }
                originalPlaylistStream = player.Stream; playlistStream = copy;
                float position = player.GetPlaybackPosition(); bool playing = player.Playing, wasPaused = player.StreamPaused;
                player.Stream = copy; expected = copy;
                player.Finished += PlaylistFinished;
                if (playing) player.Play(position);
                player.StreamPaused = wasPaused;
            }
            private void DetachPlaylist()
            {
                playlistLease++;
                if (playlistStream == null) return;
                if (GodotObject.IsInstanceValid(player))
                {
                    player.Finished -= PlaylistFinished;
                    if (OwnsPlayback)
                    {
                        float position = player.GetPlaybackPosition(); bool playing = player.Playing, wasPaused = player.StreamPaused;
                        player.Stream = originalPlaylistStream; expected = originalPlaylistStream;
                        if (playing) player.Play(position);
                        player.StreamPaused = wasPaused;
                    }
                }
                playlistStream.Dispose(); playlistStream = null; originalPlaylistStream = null;
            }
            private void PlaylistFinished()
            {
                long lease = playlistLease;
                playlistEnded = true; resumePosition = 0; resumePending = false;
                Callable.From(() => { if (!disposed && lease == playlistLease && OwnsPlayback && Available()) AdvancePlaylist(); }).CallDeferred();
            }
            private void AdvancePlaylist()
            {
                RefreshLibrary();
                int next = PlaylistNext(library.Length, FindTrackIndex(library, choice), owner.ReadPlaybackOrder(), 1);
                if (next < 0) { status.Text = "列表播放结束"; return; }
                string name = library[next];
                if (name == choice && OwnsPlayback) { playlistEnded = false; player.Play(0); player.StreamPaused = false; return; }
                if (!owner.TrySaveMenuTrack(name, out string message, context.Setting)) { status.Text = "自动切歌失败：" + message; return; }
                startRequested = true; Tick();
                if (OwnsPlayback) { playlistEnded = false; player.Play(0); player.StreamPaused = false; }
                status.Text = message;
            }
        }
    }
}
