using System.Collections.Generic;

namespace SnapEye.Services
{
    /// <summary>
    /// Single source of truth for user-visible keyboard shortcuts (overlay + Dashboard).
    /// Keep this in sync with <see cref="GlobalHotkeyService"/> registrations and
    /// <see cref="OverlayWindow.Window_KeyDown"/>.
    /// </summary>
    public static class AppKeyboardShortcuts
    {
        public sealed record Row(string Action, string Description, string Keys);

        /// <summary>All shortcuts, in display order.</summary>
        public static IReadOnlyList<Row> All { get; } = new Row[]
        {
            new("Move overlay (nudge)",
                "Press Ctrl+Alt with an arrow key (↑↓←→) to move the overlay in steps. Keys may also reach the focused app (unlike the old hook-only chord). Works globally via RegisterHotKey.",
                "Ctrl+Alt+↑ / ↓ / ← / →"),
            new("Expand Live Insights",
                "Show the main session / insights panel.",
                "Ctrl+Shift+Up"),
            new("Collapse Live Insights",
                "Hide the main session / insights panel (toolbar stays visible).",
                "Ctrl+Shift+Down"),
            new("Toggle listening (Live)",
                "Start or stop live transcription. Works even when another app is focused.",
                "Ctrl+Shift+L"),
            new("Mute / unmute microphone",
                "Stop or resume sending your microphone audio for transcription. Works globally via a low-level keyboard hook (the only way to bind this combo). You can also click the mic button next to the camera.",
                "` (Backtick) + Space"),
            new("Screenshot / OCR",
                "Capture the primary screen, run OCR, and send text to the AI. Works globally.",
                "Ctrl+Shift+S"),
            new("Copy answer",
                "Copy the current AI answer to the clipboard.",
                "Ctrl+Shift+C"),
            new("Hide or show overlay",
                "Toggle overlay visibility without closing the app.",
                "Ctrl+Shift+H"),
            new("Hide overlay",
                "Collapse the overlay (shortcut works when the overlay has keyboard focus).",
                "Esc"),
        };

        public const string ScreenCapturePrivacyNote =
            "Invisible to capture hides SnapEye windows and menus from most screen recorders " +
            "(OBS, Teams, Snipping Tool). Global shortcuts use Windows RegisterHotKey only — " +
            "SnapEye does not install a low-level keyboard hook for shortcuts. Other software " +
            "can still use its own hooks to read keys.";
    }
}
