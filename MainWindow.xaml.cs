using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;

// A program that listens to keys (received from a infrared to usb keyboard unit we can change to send whatever is most convenient)
// that sends out http requests to kodi. It must listen in the background as kodi receives keys from the same unit and has the focus.
//
// The program will also poll kodi for the current zoom and subtitle state and display it in a simple gui.
//
// The program will allow the user:
// - to change the zoom level of the video (down/up by 0.1x using Ctrl+Shift+Alt+F1/F2).
// - to change the subtitle track of the video (using Ctrl+Shift+Alt+F3/F4).
// - to toggle the subtitle track on/off (using Ctrl+Shift+Alt+F5).
// - to display the current zoom level and subtitle track in the gui.
// - show detailed logs of the requests sent to kodi and the responses received from kodi in a text box in the gui.
//
// Kodi's JSON-RPC API is used to send commands and query the current state. The program uses WPF for the GUI and Win32 API for global hotkey registration.
// The Kodi JSON-RPC API is protected by username and password, which is stored in the program's settings and used for authentication in the HTTP requests.

namespace KodiListenerGui
{
    public partial class MainWindow : Window
    {
        // Win32 API Imports
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_F1 = 0x70;
        private const uint VK_F2 = 0x71;
        private const uint VK_F3 = 0x72;
        private const uint VK_F4 = 0x73;
        private const uint VK_F5 = 0x74;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly string[] SubtitleEnabledProperty = { "subtitleenabled" };
        private static readonly string[] PlayerStatusProperties = { "subtitles", "currentsubtitle", "subtitleenabled", "speed", "time", "totaltime" };
        private static readonly string[] TitleRequestProperty = { "title" };

        private IntPtr _windowHandle;
        private HwndSource? _hwndSource;
        private DispatcherTimer? _pollTimer;
        private bool _isFullScreen = true;
        private readonly KodiSettings _settings;
        private readonly KodiClient _kodiClient;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly SemaphoreSlim _kodiOperationLock = new(1, 1);
        private readonly List<int> _registeredHotkeyIds = new();

        public MainWindow()
        {
            InitializeComponent();
            _settings = KodiSettings.Load(Log);
            _kodiClient = new KodiClient(_settings.HostUrl, _settings.Username, _settings.Password, Log);

            Loaded += (s, e) => WindowState = WindowState.Maximized;

            Log($"Application initialized. Kodi endpoint: {_settings.HostUrl}");
        }

        // In WPF, window hooks must be registered after the window source is ready
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_windowHandle);
            _hwndSource.AddHook(HwndHook); // Intercept Windows Messages

            uint modifiers = MOD_CONTROL | MOD_SHIFT | MOD_ALT;

            // Register Hotkeys tied to this window's handle, tracking only the ones that actually succeeded
            TryRegisterHotkey(1, modifiers, VK_F1, "Zoom out (Ctrl+Shift+Alt+F1)");
            TryRegisterHotkey(2, modifiers, VK_F2, "Zoom in (Ctrl+Shift+Alt+F2)");
            TryRegisterHotkey(3, modifiers, VK_F3, "Previous subtitle (Ctrl+Shift+Alt+F3)");
            TryRegisterHotkey(4, modifiers, VK_F4, "Next subtitle (Ctrl+Shift+Alt+F4)");
            TryRegisterHotkey(5, modifiers, VK_F5, "Toggle subtitle (Ctrl+Shift+Alt+F5)");

            Log(_registeredHotkeyIds.Count == 5
                ? "Global Hotkeys registered (Ctrl+Shift+Alt+F1-F5)."
                : $"Global Hotkeys: only {_registeredHotkeyIds.Count} of 5 registered; some bindings may be unavailable (see log above).");

            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += async (s, e) => await RunExclusiveAsync(FetchKodiStatusAsync, "scheduled status poll", skipIfBusy: true);
            _pollTimer.Start();
            Log($"Background polling started (every {PollInterval.TotalSeconds:0}s).");

            _ = RunExclusiveAsync(FetchKodiStatusAsync, "initial status fetch", skipIfBusy: true);
        }

        private void TryRegisterHotkey(int id, uint modifiers, uint vk, string description)
        {
            if (RegisterHotKey(_windowHandle, id, modifiers, vk))
            {
                _registeredHotkeyIds.Add(id);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Log($"Failed to register hotkey '{description}' (Win32 error {error}); it may already be bound by another application.");
            }
        }

        // Ensures scheduled polls and hotkey-driven commands never run concurrently against Kodi.
        // Scheduled polls are skipped (not queued) when busy; user-driven hotkey actions wait their turn.
        private async Task RunExclusiveAsync(Func<Task> action, string operationName, bool skipIfBusy)
        {
            bool acquired;
            try
            {
                acquired = await _kodiOperationLock.WaitAsync(skipIfBusy ? 0 : Timeout.Infinite, _shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!acquired)
            {
                Log($"Skipping {operationName}; a previous Kodi operation is still in progress.");
                return;
            }

            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // Window is closing; nothing to report.
            }
            catch (Exception ex)
            {
                Log($"Unexpected error during {operationName}: {ex.Message}");
            }
            finally
            {
                _kodiOperationLock.Release();
            }
        }

        // Lets touch users without a keyboard drop to windowed mode to reach the title bar's close button.
        private void StatusBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isFullScreen)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                Width = 1400;
                Height = 800;
                RowDebugLabel.Height = GridLength.Auto;
                RowDebugLog.Height = new GridLength(1, GridUnitType.Star);
                LblDebugLog.Visibility = Visibility.Visible;
                TxtLog.Visibility = Visibility.Visible;
                Log("Switched to windowed mode. Use the title bar's close button to exit.");
            }
            else
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                RowDebugLabel.Height = new GridLength(0);
                RowDebugLog.Height = new GridLength(0);
                LblDebugLog.Visibility = Visibility.Collapsed;
                TxtLog.Visibility = Visibility.Collapsed;
                Log("Switched to fullscreen mode.");
            }
            _isFullScreen = !_isFullScreen;
        }

        // WPF equivalent of the Win32 message loop filter
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                _ = RunExclusiveAsync(() => HandleHotkeyAsync(hotkeyId), $"hotkey {hotkeyId}", skipIfBusy: false);
                handled = true; // Tell Windows we processed this message
            }
            return IntPtr.Zero;
        }

        private async Task HandleHotkeyAsync(int id)
        {
            Log($"Hotkey ID {id} intercepted from IR Receiver.");

            switch (id)
            {
                case 1:
                    await AdjustZoomAsync(-0.01);
                    break;
                case 2:
                    await AdjustZoomAsync(0.01);
                    break;
                case 3:
                    await SetSubtitleAsync("previous");
                    break;
                case 4:
                    await SetSubtitleAsync("next");
                    break;
                case 5:
                    await ToggleSubtitleAsync();
                    break;
            }

            // Refresh UI state after driving a command
            await FetchKodiStatusAsync();

            // Delay the next automatic poll so it doesn't land right after this manual refresh
            _pollTimer?.Stop();
            _pollTimer?.Start();
        }

        private enum PlayerLookupStatus { Found, NonePlaying, ConnectionError }

        private readonly record struct PlayerLookupResult(PlayerLookupStatus Status, int PlayerId, string? ErrorMessage);

        private async Task<PlayerLookupResult> GetActivePlayerAsync()
        {
            var response = await _kodiClient.SendRequestAsync("Player.GetActivePlayers", cancellationToken: _shutdownCts.Token);
            if (!response.Success)
            {
                return new PlayerLookupResult(PlayerLookupStatus.ConnectionError, -1, response.ErrorMessage);
            }

            if (response.Result.ValueKind == JsonValueKind.Array
                && response.Result.GetArrayLength() > 0
                && TryGetInt32(response.Result[0], "playerid", out int playerId))
            {
                return new PlayerLookupResult(PlayerLookupStatus.Found, playerId, null);
            }

            return new PlayerLookupResult(PlayerLookupStatus.NonePlaying, -1, null);
        }

        private async Task<double> GetCurrentZoomAsync()
        {
            var viewMode = await _kodiClient.SendRequestAsync("Player.GetViewMode", cancellationToken: _shutdownCts.Token);
            return viewMode.Success && TryGetDouble(viewMode.Result, "zoom", out double zoom) ? zoom : 1.0;
        }

        private async Task AdjustZoomAsync(double delta)
        {
            var player = await GetActivePlayerAsync();
            if (player.Status != PlayerLookupStatus.Found)
            {
                Log(player.Status == PlayerLookupStatus.ConnectionError
                    ? $"Cannot adjust zoom; Kodi is unreachable: {player.ErrorMessage}"
                    : "No active player to adjust zoom.");
                return;
            }

            double currentZoom = await GetCurrentZoomAsync();
            double newZoom = Math.Clamp(currentZoom + delta, 0.1, 5.0);
            Log($"Adjusting zoom {currentZoom:0.00}x -> {newZoom:0.00}x");
            await _kodiClient.SendRequestAsync("Player.SetViewMode", new { viewmode = new { zoom = newZoom } }, cancellationToken: _shutdownCts.Token);
        }

        private async Task SetSubtitleAsync(string direction)
        {
            var player = await GetActivePlayerAsync();
            if (player.Status != PlayerLookupStatus.Found)
            {
                Log(player.Status == PlayerLookupStatus.ConnectionError
                    ? $"Cannot change subtitle; Kodi is unreachable: {player.ErrorMessage}"
                    : "No active player to change subtitle.");
                return;
            }

            Log($"Setting subtitle track: {direction}");
            await _kodiClient.SendRequestAsync("Player.SetSubtitle", new { playerid = player.PlayerId, subtitle = direction, enable = true }, cancellationToken: _shutdownCts.Token);
        }

        private async Task ToggleSubtitleAsync()
        {
            var player = await GetActivePlayerAsync();
            if (player.Status != PlayerLookupStatus.Found)
            {
                Log(player.Status == PlayerLookupStatus.ConnectionError
                    ? $"Cannot toggle subtitle; Kodi is unreachable: {player.ErrorMessage}"
                    : "No active player to toggle subtitle.");
                return;
            }

            var properties = await _kodiClient.SendRequestAsync("Player.GetProperties", new { playerid = player.PlayerId, properties = SubtitleEnabledProperty }, cancellationToken: _shutdownCts.Token);
            bool currentlyEnabled = properties.Success && TryGetBool(properties.Result, "subtitleenabled", out bool enabled) && enabled;

            Log($"Toggling subtitles {(currentlyEnabled ? "off" : "on")}");
            await _kodiClient.SendRequestAsync("Player.SetSubtitle", new { playerid = player.PlayerId, subtitle = currentlyEnabled ? "off" : "on" }, cancellationToken: _shutdownCts.Token);
        }

        private async Task FetchKodiStatusAsync()
        {
            Log("Polling current playback state from Kodi...");

            var player = await GetActivePlayerAsync();

            if (player.Status == PlayerLookupStatus.ConnectionError)
            {
                // Preserve whatever was last displayed instead of masking an outage as "nothing playing".
                Log($"Status refresh skipped; Kodi is unreachable: {player.ErrorMessage}");
                Dispatcher.Invoke(() => TxtPlaybackStatus.Text = "\u26a0 Connection error - showing last known state");
                return;
            }

            if (player.Status == PlayerLookupStatus.NonePlaying)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtZoom.Text = "";
                    TxtActiveSubtitle.Text = "";
                    TxtNowPlaying.Text = "Nothing playing";
                    TxtPlaybackStatus.Text = "";
                    TxtPosition.Text = "0:00 / 0:00";
                    TxtEndsAt.Text = "";
                    PbPosition.Value = 0;
                    LstSubtitles.Items.Clear();
                });
                return;
            }

            int playerId = player.PlayerId;

            // A single JSON-RPC batch replaces three sequential requests to cut latency and overlap risk.
            var batch = await _kodiClient.SendBatchAsync(new (string Method, object? Parameters)[]
            {
                ("Player.GetProperties", new { playerid = playerId, properties = PlayerStatusProperties }),
                ("Player.GetItem", new { playerid = playerId, properties = TitleRequestProperty }),
                ("Player.GetViewMode", null)
            }, _shutdownCts.Token);

            var propertiesResponse = batch[0];
            var itemResponse = batch[1];
            var viewModeResponse = batch[2];

            if (!propertiesResponse.Success && propertiesResponse.IsConnectionError)
            {
                Log($"Status refresh incomplete; Kodi is unreachable: {propertiesResponse.ErrorMessage}");
                Dispatcher.Invoke(() => TxtPlaybackStatus.Text = "\u26a0 Connection error - showing last known state");
                return;
            }

            var properties = propertiesResponse.Result;
            double zoom = viewModeResponse.Success && TryGetDouble(viewModeResponse.Result, "zoom", out double zoomValue) ? zoomValue : 1.0;

            bool subtitleEnabled = TryGetBool(properties, "subtitleenabled", out bool subtitlesOn) && subtitlesOn;

            int currentIndex = -1;
            string activeSubtitleText = "Disabled";
            if (TryGetObject(properties, "currentsubtitle", out var currentEl))
            {
                if (TryGetInt32(currentEl, "index", out int idx))
                {
                    currentIndex = idx;
                }
                if (subtitleEnabled)
                {
                    activeSubtitleText = FormatSubtitleLabel(currentEl);
                }
            }

            var subtitleLines = new List<SubtitleTrackItem>();
            if (TryGetArray(properties, "subtitles", out var subsEl))
            {
                foreach (var sub in subsEl.EnumerateArray())
                {
                    if (!TryGetInt32(sub, "index", out int index))
                    {
                        continue; // Malformed entry from Kodi; skip rather than throw.
                    }
                    bool isSelected = index == currentIndex && subtitleEnabled;
                    subtitleLines.Add(new SubtitleTrackItem
                    {
                        Text = $"{index + 1}. {FormatSubtitleLabel(sub)}",
                        IsSelected = isSelected
                    });
                }
            }

            string nowPlaying = "Nothing playing";
            if (itemResponse.Success && TryGetObject(itemResponse.Result, "item", out var itemEl))
            {
                if (TryGetString(itemEl, "label", out string label) && !string.IsNullOrEmpty(label))
                {
                    nowPlaying = label;
                }
                else if (TryGetString(itemEl, "title", out string title) && !string.IsNullOrEmpty(title))
                {
                    nowPlaying = title;
                }
            }

            int speed = TryGetInt32(properties, "speed", out int speedValue) ? speedValue : 0;
            string playbackStatus = speed == 0 ? "Paused" : speed == 1 ? "Playing" : $"Playing ({speed}x)";

            TimeSpan position = TryGetObject(properties, "time", out var timeEl) ? ParseKodiTime(timeEl) : TimeSpan.Zero;
            TimeSpan total = TryGetObject(properties, "totaltime", out var totalTimeEl) ? ParseKodiTime(totalTimeEl) : TimeSpan.Zero;

            string positionText = FormatTimeSpan(position);
            string endsAtText = "--:--";
            double progress = 0;
            if (total > TimeSpan.Zero)
            {
                positionText += $" / {FormatTimeSpan(total)}";
                progress = Math.Clamp(position.TotalSeconds / total.TotalSeconds, 0, 1);
                if (speed > 0)
                {
                    TimeSpan remaining = total - position;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }
                    endsAtText = DateTime.Now.Add(remaining).ToString("HH:mm");
                }
            }

            Dispatcher.Invoke(() =>
            {
                TxtZoom.Text = $"{zoom:0.00}x";
                TxtActiveSubtitle.Text = activeSubtitleText;
                TxtNowPlaying.Text = nowPlaying;
                TxtPlaybackStatus.Text = playbackStatus;
                TxtPosition.Text = positionText;
                TxtEndsAt.Text = endsAtText;
                PbPosition.Value = progress;
                LstSubtitles.Items.Clear();
                foreach (var line in subtitleLines)
                {
                    LstSubtitles.Items.Add(line);
                }
            });
        }

        private static TimeSpan ParseKodiTime(JsonElement timeEl)
        {
            int hours = TryGetInt32(timeEl, "hours", out int h) ? h : 0;
            int minutes = TryGetInt32(timeEl, "minutes", out int m) ? m : 0;
            int seconds = TryGetInt32(timeEl, "seconds", out int s) ? s : 0;
            return new TimeSpan(hours, minutes, seconds);
        }

        // Defensive JsonElement accessors: Kodi's response shape can vary across versions/proxies,
        // so every optional field is validated for presence and kind before being read.
        private static bool TryGetInt32(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(name, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out value);
        }

        private static bool TryGetDouble(JsonElement obj, string name, out double value)
        {
            value = 0;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetDouble();
                return true;
            }
            return false;
        }

        private static bool TryGetBool(JsonElement obj, string name, out bool value)
        {
            value = false;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop)
                && (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
            {
                value = prop.GetBoolean();
                return true;
            }
            return false;
        }

        private static bool TryGetString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString() ?? "";
                return true;
            }
            return false;
        }

        private static bool TryGetObject(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object)
            {
                value = prop;
                return true;
            }
            return false;
        }

        private static bool TryGetArray(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                value = prop;
                return true;
            }
            return false;
        }

        // Mirrors Kodi's own subtitle labeling, e.g. "English (eng, forced, default)" or just "Italiano" when no flags apply.
        private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "English",
            ["dut"] = "Dutch",
            ["nld"] = "Dutch",
            ["fre"] = "French",
            ["fra"] = "French",
            ["ger"] = "German",
            ["deu"] = "German",
            ["spa"] = "Spanish",
            ["ita"] = "Italian",
            ["por"] = "Portuguese",
            ["rus"] = "Russian",
            ["jpn"] = "Japanese",
            ["chi"] = "Chinese",
            ["zho"] = "Chinese",
            ["kor"] = "Korean",
            ["ara"] = "Arabic",
            ["hin"] = "Hindi",
            ["gre"] = "Greek",
            ["ell"] = "Greek",
            ["swe"] = "Swedish",
            ["nor"] = "Norwegian",
            ["dan"] = "Danish",
            ["fin"] = "Finnish",
            ["pol"] = "Polish",
            ["tur"] = "Turkish",
            ["heb"] = "Hebrew",
            ["tha"] = "Thai",
            ["vie"] = "Vietnamese",
            ["cze"] = "Czech",
            ["ces"] = "Czech",
            ["hun"] = "Hungarian",
            ["rum"] = "Romanian",
            ["ron"] = "Romanian",
            ["bul"] = "Bulgarian",
            ["hrv"] = "Croatian",
            ["slo"] = "Slovak",
            ["slk"] = "Slovak",
            ["slv"] = "Slovenian",
            ["ukr"] = "Ukrainian",
            ["est"] = "Estonian",
            ["lav"] = "Latvian",
            ["lit"] = "Lithuanian",
            ["ice"] = "Icelandic",
            ["isl"] = "Icelandic",
            ["gle"] = "Irish",
            ["cat"] = "Catalan",
            ["baq"] = "Basque",
            ["eus"] = "Basque",
            ["glg"] = "Galician",
            ["may"] = "Malay",
            ["msa"] = "Malay",
            ["ind"] = "Indonesian",
            ["fil"] = "Filipino",
            ["srp"] = "Serbian",
            ["mac"] = "Macedonian",
            ["mkd"] = "Macedonian",
            ["alb"] = "Albanian",
            ["sqi"] = "Albanian",
            ["tam"] = "Tamil",
            ["tel"] = "Telugu",
            ["kan"] = "Kannada",
            ["mal"] = "Malayalam",
            ["sin"] = "Sinhala",
            ["urd"] = "Urdu",
            ["pus"] = "Pashto",
            ["fas"] = "Persian",
            ["per"] = "Persian"
        };

        private static string FormatSubtitleLabel(JsonElement subtitleEl)
        {
            TryGetString(subtitleEl, "name", out string name);
            TryGetString(subtitleEl, "language", out string language);
            bool isForced = TryGetBool(subtitleEl, "isforced", out bool forced) && forced;
            bool isDefault = TryGetBool(subtitleEl, "isdefault", out bool @default) && @default;
            bool isImpaired = TryGetBool(subtitleEl, "isimpaired", out bool impaired) && impaired;

            if (string.IsNullOrEmpty(name))
            {
                if (string.IsNullOrEmpty(language))
                {
                    name = "Unknown";
                }
                else
                {
                    name = LanguageNames.TryGetValue(language, out var languageName) ? languageName : language;
                }
            }

            var flags = new List<string>();
            if (!string.IsNullOrEmpty(language))
            {
                flags.Add(language);
            }
            if (isForced)
            {
                flags.Add("forced");
            }
            if (isDefault)
            {
                flags.Add("default");
            }
            if (isImpaired)
            {
                flags.Add("sdh");
            }

            return flags.Count > 0 ? $"{name} [{string.Join(", ", flags)}]" : name;
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }

        private const int MaxLogChars = 20000;

        private void Log(string message)
        {
            // Thread-safe UI update for the TextBox
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");

                // Trim from the front so the log can't grow unbounded during long-running sessions
                if (TxtLog.Text.Length > MaxLogChars)
                {
                    int cutoff = TxtLog.Text.IndexOf('\n', TxtLog.Text.Length - MaxLogChars);
                    if (cutoff >= 0)
                    {
                        TxtLog.Text = TxtLog.Text[(cutoff + 1)..];
                    }
                }

                TxtLog.ScrollToEnd();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _shutdownCts.Cancel();
            _pollTimer?.Stop();
            _hwndSource?.RemoveHook(HwndHook);
            foreach (int id in _registeredHotkeyIds)
            {
                UnregisterHotKey(_windowHandle, id);
            }
            _kodiClient.Dispose();
            _shutdownCts.Dispose();
            base.OnClosed(e);
        }
    }
}
