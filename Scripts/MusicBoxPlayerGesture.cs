using System;
using System.Runtime.InteropServices;
using Godot;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {
        // GetSystemMetrics describes the centered DragDetect rectangle in physical pixels.
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        private static Vector2 NativeDragTolerance(Control area, Window window)
        {
            Vector2 physical = new(4, 4);
            if (OS.HasFeature("windows"))
            {
                try
                {
                    uint dpi = (uint)Math.Max(96, DisplayServer.ScreenGetDpi(window.CurrentScreen));
                    physical = new Vector2(GetSystemMetricsForDpi(68, dpi), GetSystemMetricsForDpi(69, dpi)) / 2;
                }
                // Exported hosts may trim specialized exception types, including the type
                // used by a catch clause. Keep the input method loadable with Exception only.
                catch (Exception)
                {
                    try { physical = new Vector2(GetSystemMetrics(68), GetSystemMetrics(69)) / 2; }
                    catch (Exception) { }
                }
            }
            Vector2 scale = area.Size / new Vector2(Math.Max(1, window.Size.X), Math.Max(1, window.Size.Y));
            return new Vector2(Math.Max(1, physical.X * scale.X), Math.Max(1, physical.Y * scale.Y));
        }

        private sealed class PlayerDragGesture
        {
            public bool Pressed { get; private set; }
            public bool Dragging { get; private set; }
            private Vector2 origin, initial, tolerance;
            public void Begin(Vector2 pointer, Vector2 position, Vector2 threshold)
            { Pressed = true; Dragging = false; origin = pointer; initial = position; tolerance = threshold; }
            public bool Move(Vector2 pointer, out Vector2 position)
            {
                position = initial;
                if (!Pressed) return false;
                Vector2 delta = pointer - origin;
                if (!Dragging && Math.Abs(delta.X) <= tolerance.X && Math.Abs(delta.Y) <= tolerance.Y) return false;
                Dragging = true; position += delta; return true;
            }
            public bool End()
            { bool clicked = Pressed && !Dragging; Cancel(); return clicked; }
            public void Cancel() { Pressed = false; Dragging = false; }
        }
    }
}
