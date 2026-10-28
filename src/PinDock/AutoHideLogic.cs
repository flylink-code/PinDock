namespace PinDock;

public enum AutoHideState { Hidden, Visible }

public static class AutoHideLogic
{
    public static bool ShouldStayVisible(bool popupOpen, bool dragging, bool dialogOpen, bool cursorOverDockOrPopup)
        => popupOpen || dragging || dialogOpen || cursorOverDockOrPopup;

    /// <summary>已收起时保持收起，展开只能由用户触发；已展开时在需要保持或延时未到时继续展开。</summary>
    public static AutoHideState NextState(AutoHideState current, bool stayVisible, bool hideDelayElapsed)
    {
        if (current == AutoHideState.Hidden) return AutoHideState.Hidden;
        return stayVisible || !hideDelayElapsed ? AutoHideState.Visible : AutoHideState.Hidden;
    }
}
