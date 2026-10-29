using System.Collections.Generic;
using Godot;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        private sealed partial class MenuPlayer
        {
            private readonly Dictionary<DragMenu, bool> shieldedMenus = new();
            private bool pointerOwned, inputShieldActive;
            private void InstallPointerIsolation(Node node)
            {
                if (node is Control control)
                {
                    control.GuiInput += ShieldPointerEvent;
                    control.MouseEntered += RefreshInputShield;
                    control.MouseExited += RefreshInputShield;
                }
                foreach (Node child in node.GetChildren()) InstallPointerIsolation(child);
            }
            private void ShieldPointerEvent(InputEvent input)
            {
                if (!CanInteract()) return;
                if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
                {
                    pointerOwned = mouse.Pressed;
                    if (mouse.Pressed) CaptureSceneDragging();
                    else RefreshInputShield();
                }
            }
            private void FindSceneDraggers(Node node)
            {
                if (node is DragMenu menu && !menu.IsQueuedForDeletion())
                {
                    shieldedMenus[menu] = menu.IsProcessingInput();
                    menu.mousePress = false;
                    menu.SetProcessInput(false);
                }
                foreach (Node child in node.GetChildren()) FindSceneDraggers(child);
            }
            private void CaptureSceneDragging()
            {
                if (!inputShieldActive) { inputShieldActive = true; FindSceneDraggers(tree.Root); }
                foreach (DragMenu menu in shieldedMenus.Keys)
                    if (GodotObject.IsInstanceValid(menu) && !menu.IsQueuedForDeletion())
                    { menu.mousePress = false; menu.SetProcessInput(false); }
            }
            private void ReleaseInputShield()
            {
                pointerOwned = false;
                foreach (var pair in shieldedMenus)
                    if (GodotObject.IsInstanceValid(pair.Key) && !pair.Key.IsQueuedForDeletion())
                    { pair.Key.mousePress = false; pair.Key.SetProcessInput(pair.Value); }
                shieldedMenus.Clear(); inputShieldActive = false;
            }
            private void RefreshInputShield()
            {
                if (disposed) return;
                if (!CanInteract() || !root.IsVisibleInTree() || !window.HasFocus()) { ReleaseInputShield(); return; }
                bool held = Input.IsMouseButtonPressed(MouseButton.Left);
                if (pointerOwned && !held && !gesture.Pressed) pointerOwned = false;
                Vector2 point = root.GetLocalMousePosition();
                bool hover = new Rect2(Vector2.Zero, root.Size).HasPoint(point)
                    && mask.GetBitv(new Vector2I((int)point.X, (int)point.Y));
                bool popup = tracks.GetPopup().Visible || orderSelector.GetPopup().Visible;
                // Do not steal an existing scene drag when its pointer merely crosses the player.
                if (pointerOwned || gesture.Pressed || popup || hover && !held) CaptureSceneDragging();
                else ReleaseInputShield();
            }
        }
    }
}
