namespace SnapEye
{
    /// <summary>
    /// High-level overlay lifecycle for predictable UI and cleanup.
    /// </summary>
    public enum OverlaySessionPhase
    {
        Ready,
        Connecting,
        Listening,
        Error
    }
}
