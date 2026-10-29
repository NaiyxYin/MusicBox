using System;
using Godot;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private sealed partial class MenuPlayer
        {
            private bool transitioning, startRequested;
            private Node transitionSource;
            private string boundTrack, resumeTrack;
            private float savedPosition;
            private bool savedPaused, savedPlaying, resumePending;
            private Node ReadySceneRoot(string screen)
            {
                Node current = tree.CurrentScene;
                if (GodotObject.IsInstanceValid(current) && current.IsInsideTree() && !current.IsQueuedForDeletion()
                    && current.IsNodeReady() && MatchesScreen(current, screen)) return current;
                foreach (Node node in owner._screenExits.Keys)
                    if (GodotObject.IsInstanceValid(node) && node.IsInsideTree() && !node.IsQueuedForDeletion()
                        && node.IsNodeReady() && MatchesScreen(node, screen)) return node;
                return FindReadyRoot(tree.Root, screen);
            }
            private static Node FindReadyRoot(Node root, string screen)
            {
                if (root.IsQueuedForDeletion()) return null;
                if (root.IsNodeReady() && MatchesScreen(root, screen)) return root;
                foreach (Node child in root.GetChildren())
                { var found = FindReadyRoot(child, screen); if (found != null) return found; }
                return null;
            }
            private bool TransitionReady()
            {
                if (!transitioning) return true;
                if (SceneManager.Instance == null || SceneManager.Instance.isLoading) return false;
                Node next = ReadySceneRoot(SceneManager.CurrentScene);
                if (next == null || ReferenceEquals(next, transitionSource)) return false;
                transitioning = false; transitionSource = null; context = null; active = false;
                return true;
            }
            private string TrackIdentity()
            {
                if (context == null) return null;
                string file = owner.SlotPath(owner.ConfiguredMenuTrack(context.Setting));
                if (file == null) return null;
                return file + "#" + (owner._audioCache.TryGetValue(file, out var cached) ? cached.Stamp : "");
            }
            private void RememberPlayback()
            {
                if (!OwnsPlayback || boundTrack == null || playlistEnded) return;
                resumeTrack = boundTrack; savedPosition = player.GetPlaybackPosition();
                savedPaused = player.StreamPaused; savedPlaying = player.Playing; resumePending = true;
            }
            private void RestoreBookmark()
            {
                if (resumePending && boundTrack != null && boundTrack == resumeTrack && OwnsPlayback)
                {
                    float length = (float)player.Stream.GetLength();
                    float position = savedPosition >= 0 && savedPosition < length ? savedPosition : 0;
                    if (savedPlaying || savedPaused) player.Play(position);
                    player.StreamPaused = savedPaused; paused = savedPaused; resumePosition = position;
                }
                resumePending = false;
            }
            private bool NormalAudioReset()
            {
                if (!GodotObject.IsInstanceValid(player) || player.IsQueuedForDeletion() || player.Stream == null) return true;
                if (ResourceManager.Instance == null || !ResourceManager.Instance.AUDIOS.TryGetValue(context.Key, out Resource current)) return true;
                // A host refresh can replace our private playlist copy with its original stream.
                if (ReferenceEquals(current, registry) && ReferenceEquals(player.Stream, current)) return true;
                // A known MusicBox scene/configuration update is not an external takeover.
                if (!ReferenceEquals(current, registry))
                    foreach (var entry in owner._menuEntries)
                        if (entry.Screen == context.Screen && ReferenceEquals(current, entry.Stream)) return true;
                return false;
            }
            private void RecoverAudioBinding()
            {
                bool normal = NormalAudioReset();
                DetachPlaylist(); RestoreProcessMode(); player = null; expected = null; registry = null;
                blocked = !normal;
                status.Text = normal ? "正在同步界面音乐" : "音乐来源发生变化，点击播放可重新同步";
            }
        }
    }
}
