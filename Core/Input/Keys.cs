// Core/Input/Keys.cs
using Avalonia.Input;

namespace Game_Engine.Core.Input
{
    /// <summary>
    /// Engine-level keys (platform-agnostic). Keep stable names.
    /// </summary>
    public enum KeyCode
    {
        None = 0,

        // Alphanumeric rows
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

        // Numpad
        NumPad0, NumPad1, NumPad2, NumPad3, NumPad4,
        NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
        Multiply, Add, Separator, Subtract, Decimal, Divide,

        // Function keys
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10,
        F11, F12, F13, F14, F15, F16, F17, F18, F19, F20,
        F21, F22, F23, F24,

        // Navigation / editing
        Backspace, Tab, Clear, Enter, Pause, CapsLock, Escape, Space,
        PageUp, PageDown, End, Home,
        LeftArrow, UpArrow, RightArrow, DownArrow,
        PrintScreen, Insert, Delete, Help,

        // Locks / system
        NumLock, ScrollLock,
        LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
        LeftWindows, RightWindows, Apps, Sleep,

        // Browser keys
        BrowserBack, BrowserForward, BrowserRefresh, BrowserStop,
        BrowserSearch, BrowserFavorites, BrowserHome,

        // Media keys
        VolumeMute, VolumeDown, VolumeUp,
        MediaNextTrack, MediaPreviousTrack, MediaStop, MediaPlayPause,
        LaunchMail, SelectMedia, LaunchApp1, LaunchApp2,

        // OEM/punctuation (US + common variants)
        OemSemicolon,     // ;
        OemPlus,          // =/+ (main row)
        OemComma,         // ,
        OemMinus,         // -/_
        OemPeriod,        // .
        OemQuestion,      // /?
        OemTilde,         // `~
        OemOpenBrackets,  // [{
        OemCloseBrackets, // ]}
        OemPipe,          // \|
        OemQuotes,        // '"
        Oem8,             // Misc OEM
        OemBackslash      // OEM 102 (< > | on ISO)
    }

    public enum MouseButton
    {
        Left = 0,
        Right = 1,
        Middle = 2,
    }

    /// <summary>
    /// Centralized conversion between Avalonia Key and engine KeyCode.
    /// Add new cases here once and the whole app benefits.
    /// </summary>
    public static class KeyMap
    {
        public static bool TryFromAvalonia(Key key, out KeyCode code)
        {
            switch (key)
            {
                // Digits (top row)
                case Key.D0: code = KeyCode.D0; return true;
                case Key.D1: code = KeyCode.D1; return true;
                case Key.D2: code = KeyCode.D2; return true;
                case Key.D3: code = KeyCode.D3; return true;
                case Key.D4: code = KeyCode.D4; return true;
                case Key.D5: code = KeyCode.D5; return true;
                case Key.D6: code = KeyCode.D6; return true;
                case Key.D7: code = KeyCode.D7; return true;
                case Key.D8: code = KeyCode.D8; return true;
                case Key.D9: code = KeyCode.D9; return true;

                // Letters
                case Key.A: code = KeyCode.A; return true;
                case Key.B: code = KeyCode.B; return true;
                case Key.C: code = KeyCode.C; return true;
                case Key.D: code = KeyCode.D; return true;
                case Key.E: code = KeyCode.E; return true;
                case Key.F: code = KeyCode.F; return true;
                case Key.G: code = KeyCode.G; return true;
                case Key.H: code = KeyCode.H; return true;
                case Key.I: code = KeyCode.I; return true;
                case Key.J: code = KeyCode.J; return true;
                case Key.K: code = KeyCode.K; return true;
                case Key.L: code = KeyCode.L; return true;
                case Key.M: code = KeyCode.M; return true;
                case Key.N: code = KeyCode.N; return true;
                case Key.O: code = KeyCode.O; return true;
                case Key.P: code = KeyCode.P; return true;
                case Key.Q: code = KeyCode.Q; return true;
                case Key.R: code = KeyCode.R; return true;
                case Key.S: code = KeyCode.S; return true;
                case Key.T: code = KeyCode.T; return true;
                case Key.U: code = KeyCode.U; return true;
                case Key.V: code = KeyCode.V; return true;
                case Key.W: code = KeyCode.W; return true;
                case Key.X: code = KeyCode.X; return true;
                case Key.Y: code = KeyCode.Y; return true;
                case Key.Z: code = KeyCode.Z; return true;

                // Numpad
                case Key.NumPad0: code = KeyCode.NumPad0; return true;
                case Key.NumPad1: code = KeyCode.NumPad1; return true;
                case Key.NumPad2: code = KeyCode.NumPad2; return true;
                case Key.NumPad3: code = KeyCode.NumPad3; return true;
                case Key.NumPad4: code = KeyCode.NumPad4; return true;
                case Key.NumPad5: code = KeyCode.NumPad5; return true;
                case Key.NumPad6: code = KeyCode.NumPad6; return true;
                case Key.NumPad7: code = KeyCode.NumPad7; return true;
                case Key.NumPad8: code = KeyCode.NumPad8; return true;
                case Key.NumPad9: code = KeyCode.NumPad9; return true;
                case Key.Multiply: code = KeyCode.Multiply; return true;
                case Key.Add: code = KeyCode.Add; return true;
                case Key.Separator: code = KeyCode.Separator; return true;
                case Key.Subtract: code = KeyCode.Subtract; return true;
                case Key.Decimal: code = KeyCode.Decimal; return true;
                case Key.Divide: code = KeyCode.Divide; return true;

                // Function keys
                case Key.F1: code = KeyCode.F1; return true;
                case Key.F2: code = KeyCode.F2; return true;
                case Key.F3: code = KeyCode.F3; return true;
                case Key.F4: code = KeyCode.F4; return true;
                case Key.F5: code = KeyCode.F5; return true;
                case Key.F6: code = KeyCode.F6; return true;
                case Key.F7: code = KeyCode.F7; return true;
                case Key.F8: code = KeyCode.F8; return true;
                case Key.F9: code = KeyCode.F9; return true;
                case Key.F10: code = KeyCode.F10; return true;
                case Key.F11: code = KeyCode.F11; return true;
                case Key.F12: code = KeyCode.F12; return true;
                case Key.F13: code = KeyCode.F13; return true;
                case Key.F14: code = KeyCode.F14; return true;
                case Key.F15: code = KeyCode.F15; return true;
                case Key.F16: code = KeyCode.F16; return true;
                case Key.F17: code = KeyCode.F17; return true;
                case Key.F18: code = KeyCode.F18; return true;
                case Key.F19: code = KeyCode.F19; return true;
                case Key.F20: code = KeyCode.F20; return true;
                case Key.F21: code = KeyCode.F21; return true;
                case Key.F22: code = KeyCode.F22; return true;
                case Key.F23: code = KeyCode.F23; return true;
                case Key.F24: code = KeyCode.F24; return true;

                // Navigation / editing
                case Key.Back: code = KeyCode.Backspace; return true;
                case Key.Tab: code = KeyCode.Tab; return true;
                case Key.Clear: code = KeyCode.Clear; return true;
                case Key.Enter: code = KeyCode.Enter; return true;
                case Key.Pause: code = KeyCode.Pause; return true;
                case Key.CapsLock: code = KeyCode.CapsLock; return true;
                case Key.Escape: code = KeyCode.Escape; return true;
                case Key.Space: code = KeyCode.Space; return true;
                case Key.PageUp: code = KeyCode.PageUp; return true;
                case Key.PageDown: code = KeyCode.PageDown; return true;
                case Key.End: code = KeyCode.End; return true;
                case Key.Home: code = KeyCode.Home; return true;
                case Key.Left: code = KeyCode.LeftArrow; return true;
                case Key.Up: code = KeyCode.UpArrow; return true;
                case Key.Right: code = KeyCode.RightArrow; return true;
                case Key.Down: code = KeyCode.DownArrow; return true;
                case Key.Snapshot: code = KeyCode.PrintScreen; return true;
                case Key.Insert: code = KeyCode.Insert; return true;
                case Key.Delete: code = KeyCode.Delete; return true;
                case Key.Help: code = KeyCode.Help; return true;

                // Locks / system
                case Key.NumLock: code = KeyCode.NumLock; return true;
                case Key.Scroll: code = KeyCode.ScrollLock; return true;
                case Key.LeftShift: code = KeyCode.LeftShift; return true;
                case Key.RightShift: code = KeyCode.RightShift; return true;
                case Key.LeftCtrl: code = KeyCode.LeftCtrl; return true;
                case Key.RightCtrl: code = KeyCode.RightCtrl; return true;
                case Key.LeftAlt: code = KeyCode.LeftAlt; return true;
                case Key.RightAlt: code = KeyCode.RightAlt; return true;
                case Key.LWin: code = KeyCode.LeftWindows; return true;
                case Key.RWin: code = KeyCode.RightWindows; return true;
                case Key.Apps: code = KeyCode.Apps; return true;
                case Key.Sleep: code = KeyCode.Sleep; return true;

                // Browser
                case Key.BrowserBack: code = KeyCode.BrowserBack; return true;
                case Key.BrowserForward: code = KeyCode.BrowserForward; return true;
                case Key.BrowserRefresh: code = KeyCode.BrowserRefresh; return true;
                case Key.BrowserStop: code = KeyCode.BrowserStop; return true;
                case Key.BrowserSearch: code = KeyCode.BrowserSearch; return true;
                case Key.BrowserFavorites: code = KeyCode.BrowserFavorites; return true;
                case Key.BrowserHome: code = KeyCode.BrowserHome; return true;

                // Media
                case Key.VolumeMute: code = KeyCode.VolumeMute; return true;
                case Key.VolumeDown: code = KeyCode.VolumeDown; return true;
                case Key.VolumeUp: code = KeyCode.VolumeUp; return true;
                case Key.MediaNextTrack: code = KeyCode.MediaNextTrack; return true;
                case Key.MediaPreviousTrack: code = KeyCode.MediaPreviousTrack; return true;
                case Key.MediaStop: code = KeyCode.MediaStop; return true;
                case Key.MediaPlayPause: code = KeyCode.MediaPlayPause; return true;
                case Key.LaunchMail: code = KeyCode.LaunchMail; return true;
                case Key.SelectMedia: code = KeyCode.SelectMedia; return true;
                case Key.LaunchApplication1: code = KeyCode.LaunchApp1; return true;
                case Key.LaunchApplication2: code = KeyCode.LaunchApp2; return true;

                // OEM / punctuation
                case Key.OemSemicolon: code = KeyCode.OemSemicolon; return true;    // ;
                case Key.OemPlus: code = KeyCode.OemPlus; return true;         // =/+
                case Key.OemComma: code = KeyCode.OemComma; return true;        // ,
                case Key.OemMinus: code = KeyCode.OemMinus; return true;        // -/_
                case Key.OemPeriod: code = KeyCode.OemPeriod; return true;       // .
                case Key.OemQuestion: code = KeyCode.OemQuestion; return true;     // /?
                case Key.OemTilde: code = KeyCode.OemTilde; return true;        // `~
                case Key.OemOpenBrackets: code = KeyCode.OemOpenBrackets; return true; // [{
                case Key.OemCloseBrackets: code = KeyCode.OemCloseBrackets; return true;// ]}
                case Key.OemPipe: code = KeyCode.OemPipe; return true;         // \|
                case Key.OemQuotes: code = KeyCode.OemQuotes; return true;       // '"
                case Key.Oem8: code = KeyCode.Oem8; return true;            // misc OEM
                case Key.OemBackslash: code = KeyCode.OemBackslash; return true;    // OEM 102 (< > |)
            }
            code = KeyCode.None;
            return false;
        }

        public static KeyCode FromAvalonia(Key key)
        {
            KeyCode c;
            return TryFromAvalonia(key, out c) ? c : KeyCode.None;
        }

        // Reverse map (useful for showing bindings w/ platform labels) not used yet
        public static bool TryToAvalonia(KeyCode code, out Key key)
        {
            switch (code)
            {
                // Digits
                case KeyCode.D0: key = Key.D0; return true;
                case KeyCode.D1: key = Key.D1; return true;
                case KeyCode.D2: key = Key.D2; return true;
                case KeyCode.D3: key = Key.D3; return true;
                case KeyCode.D4: key = Key.D4; return true;
                case KeyCode.D5: key = Key.D5; return true;
                case KeyCode.D6: key = Key.D6; return true;
                case KeyCode.D7: key = Key.D7; return true;
                case KeyCode.D8: key = Key.D8; return true;
                case KeyCode.D9: key = Key.D9; return true;

                // Letters
                case KeyCode.A: key = Key.A; return true;
                case KeyCode.B: key = Key.B; return true;
                case KeyCode.C: key = Key.C; return true;
                case KeyCode.D: key = Key.D; return true;
                case KeyCode.E: key = Key.E; return true;
                case KeyCode.F: key = Key.F; return true;
                case KeyCode.G: key = Key.G; return true;
                case KeyCode.H: key = Key.H; return true;
                case KeyCode.I: key = Key.I; return true;
                case KeyCode.J: key = Key.J; return true;
                case KeyCode.K: key = Key.K; return true;
                case KeyCode.L: key = Key.L; return true;
                case KeyCode.M: key = Key.M; return true;
                case KeyCode.N: key = Key.N; return true;
                case KeyCode.O: key = Key.O; return true;
                case KeyCode.P: key = Key.P; return true;
                case KeyCode.Q: key = Key.Q; return true;
                case KeyCode.R: key = Key.R; return true;
                case KeyCode.S: key = Key.S; return true;
                case KeyCode.T: key = Key.T; return true;
                case KeyCode.U: key = Key.U; return true;
                case KeyCode.V: key = Key.V; return true;
                case KeyCode.W: key = Key.W; return true;
                case KeyCode.X: key = Key.X; return true;
                case KeyCode.Y: key = Key.Y; return true;
                case KeyCode.Z: key = Key.Z; return true;

                // Numpad
                case KeyCode.NumPad0: key = Key.NumPad0; return true;
                case KeyCode.NumPad1: key = Key.NumPad1; return true;
                case KeyCode.NumPad2: key = Key.NumPad2; return true;
                case KeyCode.NumPad3: key = Key.NumPad3; return true;
                case KeyCode.NumPad4: key = Key.NumPad4; return true;
                case KeyCode.NumPad5: key = Key.NumPad5; return true;
                case KeyCode.NumPad6: key = Key.NumPad6; return true;
                case KeyCode.NumPad7: key = Key.NumPad7; return true;
                case KeyCode.NumPad8: key = Key.NumPad8; return true;
                case KeyCode.NumPad9: key = Key.NumPad9; return true;
                case KeyCode.Multiply: key = Key.Multiply; return true;
                case KeyCode.Add: key = Key.Add; return true;
                case KeyCode.Separator: key = Key.Separator; return true;
                case KeyCode.Subtract: key = Key.Subtract; return true;
                case KeyCode.Decimal: key = Key.Decimal; return true;
                case KeyCode.Divide: key = Key.Divide; return true;

                // Function
                case KeyCode.F1: key = Key.F1; return true;
                case KeyCode.F2: key = Key.F2; return true;
                case KeyCode.F3: key = Key.F3; return true;
                case KeyCode.F4: key = Key.F4; return true;
                case KeyCode.F5: key = Key.F5; return true;
                case KeyCode.F6: key = Key.F6; return true;
                case KeyCode.F7: key = Key.F7; return true;
                case KeyCode.F8: key = Key.F8; return true;
                case KeyCode.F9: key = Key.F9; return true;
                case KeyCode.F10: key = Key.F10; return true;
                case KeyCode.F11: key = Key.F11; return true;
                case KeyCode.F12: key = Key.F12; return true;
                case KeyCode.F13: key = Key.F13; return true;
                case KeyCode.F14: key = Key.F14; return true;
                case KeyCode.F15: key = Key.F15; return true;
                case KeyCode.F16: key = Key.F16; return true;
                case KeyCode.F17: key = Key.F17; return true;
                case KeyCode.F18: key = Key.F18; return true;
                case KeyCode.F19: key = Key.F19; return true;
                case KeyCode.F20: key = Key.F20; return true;
                case KeyCode.F21: key = Key.F21; return true;
                case KeyCode.F22: key = Key.F22; return true;
                case KeyCode.F23: key = Key.F23; return true;
                case KeyCode.F24: key = Key.F24; return true;

                // Navigation / editing
                case KeyCode.Backspace: key = Key.Back; return true;
                case KeyCode.Tab: key = Key.Tab; return true;
                case KeyCode.Clear: key = Key.Clear; return true;
                case KeyCode.Enter: key = Key.Enter; return true;
                case KeyCode.Pause: key = Key.Pause; return true;
                case KeyCode.CapsLock: key = Key.CapsLock; return true;
                case KeyCode.Escape: key = Key.Escape; return true;
                case KeyCode.Space: key = Key.Space; return true;
                case KeyCode.PageUp: key = Key.PageUp; return true;
                case KeyCode.PageDown: key = Key.PageDown; return true;
                case KeyCode.End: key = Key.End; return true;
                case KeyCode.Home: key = Key.Home; return true;
                case KeyCode.LeftArrow: key = Key.Left; return true;
                case KeyCode.UpArrow: key = Key.Up; return true;
                case KeyCode.RightArrow: key = Key.Right; return true;
                case KeyCode.DownArrow: key = Key.Down; return true;
                case KeyCode.PrintScreen: key = Key.Snapshot; return true;
                case KeyCode.Insert: key = Key.Insert; return true;
                case KeyCode.Delete: key = Key.Delete; return true;
                case KeyCode.Help: key = Key.Help; return true;

                // Locks / system
                case KeyCode.NumLock: key = Key.NumLock; return true;
                case KeyCode.ScrollLock: key = Key.Scroll; return true;
                case KeyCode.LeftShift: key = Key.LeftShift; return true;
                case KeyCode.RightShift: key = Key.RightShift; return true;
                case KeyCode.LeftCtrl: key = Key.LeftCtrl; return true;
                case KeyCode.RightCtrl: key = Key.RightCtrl; return true;
                case KeyCode.LeftAlt: key = Key.LeftAlt; return true;
                case KeyCode.RightAlt: key = Key.RightAlt; return true;
                case KeyCode.LeftWindows: key = Key.LWin; return true;
                case KeyCode.RightWindows: key = Key.RWin; return true;
                case KeyCode.Apps: key = Key.Apps; return true;
                case KeyCode.Sleep: key = Key.Sleep; return true;

                // Browser
                case KeyCode.BrowserBack: key = Key.BrowserBack; return true;
                case KeyCode.BrowserForward: key = Key.BrowserForward; return true;
                case KeyCode.BrowserRefresh: key = Key.BrowserRefresh; return true;
                case KeyCode.BrowserStop: key = Key.BrowserStop; return true;
                case KeyCode.BrowserSearch: key = Key.BrowserSearch; return true;
                case KeyCode.BrowserFavorites: key = Key.BrowserFavorites; return true;
                case KeyCode.BrowserHome: key = Key.BrowserHome; return true;

                // Media
                case KeyCode.VolumeMute: key = Key.VolumeMute; return true;
                case KeyCode.VolumeDown: key = Key.VolumeDown; return true;
                case KeyCode.VolumeUp: key = Key.VolumeUp; return true;
                case KeyCode.MediaNextTrack: key = Key.MediaNextTrack; return true;
                case KeyCode.MediaPreviousTrack: key = Key.MediaPreviousTrack; return true;
                case KeyCode.MediaStop: key = Key.MediaStop; return true;
                case KeyCode.MediaPlayPause: key = Key.MediaPlayPause; return true;
                case KeyCode.LaunchMail: key = Key.LaunchMail; return true;
                case KeyCode.SelectMedia: key = Key.SelectMedia; return true;
                case KeyCode.LaunchApp1: key = Key.LaunchApplication1; return true;
                case KeyCode.LaunchApp2: key = Key.LaunchApplication2; return true;

                // OEM / punctuation
                case KeyCode.OemSemicolon: key = Key.OemSemicolon; return true;
                case KeyCode.OemPlus: key = Key.OemPlus; return true;
                case KeyCode.OemComma: key = Key.OemComma; return true;
                case KeyCode.OemMinus: key = Key.OemMinus; return true;
                case KeyCode.OemPeriod: key = Key.OemPeriod; return true;
                case KeyCode.OemQuestion: key = Key.OemQuestion; return true;
                case KeyCode.OemTilde: key = Key.OemTilde; return true;
                case KeyCode.OemOpenBrackets: key = Key.OemOpenBrackets; return true;
                case KeyCode.OemCloseBrackets: key = Key.OemCloseBrackets; return true;
                case KeyCode.OemPipe: key = Key.OemPipe; return true;
                case KeyCode.OemQuotes: key = Key.OemQuotes; return true;
                case KeyCode.Oem8: key = Key.Oem8; return true;
                case KeyCode.OemBackslash: key = Key.OemBackslash; return true;
            }
            key = default(Key);
            return false;
        }
    }
}
