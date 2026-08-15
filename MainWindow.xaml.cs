using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        [DllImport("user32.dll")]
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
        private bool? _kodiReachable;
        private bool _isFullScreen = true;
        private readonly HttpClient _httpClient = new();
        private readonly KodiSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _settings = KodiSettings.Load();

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                byte[] credentials = Encoding.ASCII.GetBytes($"{_settings.Username}:{_settings.Password}");
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
            }

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

            // Register Hotkeys tied to this window's handle
            RegisterHotKey(_windowHandle, 1, modifiers, VK_F1);
            RegisterHotKey(_windowHandle, 2, modifiers, VK_F2);
            RegisterHotKey(_windowHandle, 3, modifiers, VK_F3);
            RegisterHotKey(_windowHandle, 4, modifiers, VK_F4);
            RegisterHotKey(_windowHandle, 5, modifiers, VK_F5);

            Log("Global Hotkeys registered (Ctrl+Shift+Alt+F1-F5).");

            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += async (s, e) => await FetchKodiStatusAsync();
            _pollTimer.Start();
            Log($"Background polling started (every {PollInterval.TotalSeconds:0}s).");

            _ = FetchKodiStatusAsync();
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
                _ = HandleHotkeyAsync(hotkeyId);
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

        private async Task<int?> GetActivePlayerIdAsync()
        {
            var players = await SendKodiRequestAsync("Player.GetActivePlayers");
            if (players.ValueKind == JsonValueKind.Array && players.GetArrayLength() > 0)
            {
                return players[0].GetProperty("playerid").GetInt32();
            }
            return null;
        }

        private async Task<double> GetCurrentZoomAsync()
        {
            var viewMode = await SendKodiRequestAsync("Player.GetViewMode");
            return viewMode.ValueKind == JsonValueKind.Object && viewMode.TryGetProperty("zoom", out var zoomEl)
                ? zoomEl.GetDouble()
                : 1.0;
        }

        private async Task AdjustZoomAsync(double delta)
        {
            if (await GetActivePlayerIdAsync() is not int playerId)
            {
                Log("No active player to adjust zoom.");
                return;
            }

            double currentZoom = await GetCurrentZoomAsync();
            double newZoom = Math.Clamp(currentZoom + delta, 0.1, 5.0);
            Log($"Adjusting zoom {currentZoom:0.00}x -> {newZoom:0.00}x");
            await SendKodiRequestAsync("Player.SetViewMode", new { viewmode = new { zoom = newZoom } });
        }

        private async Task SetSubtitleAsync(string direction)
        {
            if (await GetActivePlayerIdAsync() is not int playerId)
            {
                Log("No active player to change subtitle.");
                return;
            }

            Log($"Setting subtitle track: {direction}");
            await SendKodiRequestAsync("Player.SetSubtitle", new { playerid = playerId, subtitle = direction, enable = true });
        }

        private async Task ToggleSubtitleAsync()
        {
            if (await GetActivePlayerIdAsync() is not int playerId)
            {
                Log("No active player to toggle subtitle.");
                return;
            }

            var properties = await SendKodiRequestAsync("Player.GetProperties", new { playerid = playerId, properties = SubtitleEnabledProperty });
            bool currentlyEnabled = properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("subtitleenabled", out var enabledEl)
                && enabledEl.GetBoolean();

            Log($"Toggling subtitles {(currentlyEnabled ? "off" : "on")}");
            await SendKodiRequestAsync("Player.SetSubtitle", new { playerid = playerId, subtitle = currentlyEnabled ? "off" : "on" });
        }

        private async Task FetchKodiStatusAsync()
        {
            Log("Polling current playback state from Kodi...");

            if (await GetActivePlayerIdAsync() is not int playerId)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtZoom.Text = "N/A";
                    TxtActiveSubtitle.Text = "No active player";
                    TxtNowPlaying.Text = "Nothing playing";
                    TxtPlaybackStatus.Text = "-";
                    TxtPosition.Text = "-";
                    PbPosition.Value = 0;
                    LstSubtitles.Items.Clear();
                });
                return;
            }

            var properties = await SendKodiRequestAsync("Player.GetProperties", new
            {
                playerid = playerId,
                properties = PlayerStatusProperties
            });
            var item = await SendKodiRequestAsync("Player.GetItem", new { playerid = playerId, properties = TitleRequestProperty });
            double zoom = await GetCurrentZoomAsync();

            bool subtitleEnabled = properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("subtitleenabled", out var enabledEl)
                && enabledEl.GetBoolean();

            int currentIndex = -1;
            string activeSubtitleText = "Disabled";
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("currentsubtitle", out var currentEl)
                && currentEl.ValueKind == JsonValueKind.Object)
            {
                if (currentEl.TryGetProperty("index", out var idxEl))
                {
                    currentIndex = idxEl.GetInt32();
                }
                if (subtitleEnabled && currentEl.TryGetProperty("name", out var nameEl))
                {
                    activeSubtitleText = nameEl.GetString() ?? "Unknown";
                }
            }

            var subtitleLines = new List<SubtitleTrackItem>();
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("subtitles", out var subsEl)
                && subsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in subsEl.EnumerateArray())
                {
                    int index = sub.GetProperty("index").GetInt32();
                    string name = sub.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string language = sub.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
                    bool isSelected = index == currentIndex && subtitleEnabled;
                    subtitleLines.Add(new SubtitleTrackItem
                    {
                        Text = $"{index + 1}. {name} ({language})",
                        IsSelected = isSelected
                    });
                }
            }

            string nowPlaying = "Nothing playing";
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("item", out var itemEl)
                && itemEl.ValueKind == JsonValueKind.Object)
            {
                if (itemEl.TryGetProperty("label", out var labelEl) && !string.IsNullOrEmpty(labelEl.GetString()))
                {
                    nowPlaying = labelEl.GetString()!;
                }
                else if (itemEl.TryGetProperty("title", out var titleEl) && !string.IsNullOrEmpty(titleEl.GetString()))
                {
                    nowPlaying = titleEl.GetString()!;
                }
            }

            int speed = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("speed", out var speedEl)
                ? speedEl.GetInt32()
                : 0;
            string playbackStatus = speed == 0 ? "Paused" : speed == 1 ? "Playing" : $"Playing ({speed}x)";

            TimeSpan position = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("time", out var timeEl)
                ? ParseKodiTime(timeEl)
                : TimeSpan.Zero;
            TimeSpan total = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("totaltime", out var totalTimeEl)
                ? ParseKodiTime(totalTimeEl)
                : TimeSpan.Zero;

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
            int hours = timeEl.TryGetProperty("hours", out var h) ? h.GetInt32() : 0;
            int minutes = timeEl.TryGetProperty("minutes", out var m) ? m.GetInt32() : 0;
            int seconds = timeEl.TryGetProperty("seconds", out var s) ? s.GetInt32() : 0;
            return new TimeSpan(hours, minutes, seconds);
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }

        private async Task<JsonElement> SendKodiRequestAsync(string method, object? parameters = null)
        {
            var payload = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters ?? new { },
                id = 1
            };
            string jsonPayload = JsonSerializer.Serialize(payload);

            try
            {
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_settings.HostUrl, content);
                string body = await response.Content.ReadAsStringAsync();
                Log($"{method} -> {response.StatusCode}");

                if (_kodiReachable != true)
                {
                    Log("Kodi is reachable.");
                }
                _kodiReachable = true;

                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("result", out var result))
                {
                    return result.Clone();
                }
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    Log($"Kodi error: {error}");
                }
            }
            catch (Exception ex)
            {
                if (_kodiReachable != false)
                {
                    Log($"Kodi is unavailable: {ex.Message}");
                }
                _kodiReachable = false;
            }

            return default;
        }

        private void Log(string message)
        {
            // Thread-safe UI update for the TextBox
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TxtLog.ScrollToEnd();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer?.Stop();
            _hwndSource?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, 1);
            UnregisterHotKey(_windowHandle, 2);
            UnregisterHotKey(_windowHandle, 3);
            UnregisterHotKey(_windowHandle, 4);
            UnregisterHotKey(_windowHandle, 5);
            base.OnClosed(e);
        }
    }
}
