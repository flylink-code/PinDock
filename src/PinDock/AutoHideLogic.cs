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

    /// <summary>收起状态下通过光标进入边缘触发条区域来兜底唤出，防止触发条被遮挡或漏触发 MouseEnter 时无法呼出。</summary>
    public static bool ShouldRevealFromEdge(
        AutoHideState current,
        bool suppressed,
        bool cursorOverEdge,
        bool requireCursorExitFirst)
    {
        if (current != AutoHideState.Hidden || suppressed || requireCursorExitFirst)
            return false;
        return cursorOverEdge;
    }
}
