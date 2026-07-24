using Game;

namespace SurvivalcraftGenius.UI;

internal static class InputIsolation
{
    /// <summary>
    /// Suppresses the remainder of this frame's shared input so HUD widgets that
    /// update after the caller (the multiplayer chat polls Enter directly) see
    /// nothing — while preserving the cross-frame press tracking that click
    /// detection needs. A bare WidgetInput.Clear() here would wipe
    /// m_mouseDownPoint every frame and no button click could ever complete.
    /// </summary>
    public static void ShieldRestOfFrame(WidgetInput input)
    {
        var mouseDown = input.m_mouseDownPoint;
        var mouseDrag = input.m_mouseDragInProgress;
        var padDown = input.m_padDownPoint;
        var padDrag = input.m_padDragInProgress;
        var touchCleared = input.m_touchCleared;
        input.Clear();
        input.m_mouseDownPoint = mouseDown;
        input.m_mouseDragInProgress = mouseDrag;
        input.m_padDownPoint = padDown;
        input.m_padDragInProgress = padDrag;
        input.m_touchCleared = touchCleared;
    }
}
