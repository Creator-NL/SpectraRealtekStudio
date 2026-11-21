using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SpectraRealtekStudio.Core.Models;
using SpectraRealtekStudio.Core.Services;

namespace SpectraRealtekStudio.App;

public partial class MainWindow : Window
{
    private readonly RealtekStudioService _studio = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _jackSenseTimer;

    private sealed class JackRowUiState
    {
        public required JackPinInfo Pin { get; set; }
        public required TextBlock PlugBadge { get; init; }
        public required ComboBox RoleCombo { get; init; }
        public required Button RetaskButton { get; init; }
    }

    private readonly Dictionary<byte, JackRowUiState> _jackRowsByWidget = new();
    private string _lastEndpointFingerprint = string.Empty;
    private bool _isJackSensePolling;
    private bool _isRetaskInProgress;

    private readonly Slider[] _eqBandSliders = new Slider[10];
    private readonly TextBlock[] _eqBandValueLabels = new TextBlock[10];

    private readonly Slider[] _rcDistanceSliders = new Slider[8];
    private readonly TextBlock[] _rcDistanceLabels = new TextBlock[8];
    private readonly Slider[] _rcGainSliders = new Slider[8];
    private readonly TextBlock[] _rcGainLabels = new TextBlock[8];

    private static readonly string[] RoomSpeakerNames =
    {
        "Front Left (FL)",
        "Front Right (FR)",
        "Center (C)",
        "Subwoofer (LFE)",
        "Rear Left (RL)",
        "Rear Right (RR)",
        "Side Left (SL)",
        "Side Right (SR)"
    };

    private List<RealtekEndpointInfo> _playbackEndpoints = new();
    private List<RealtekEndpointInfo> _captureEndpoints = new();
    private RealtekEndpointInfo? _activePlaybackEndpoint;
    private RealtekEndpointInfo? _activeCaptureEndpoint;

    private RealtekPlaybackState _currentPlaybackState = new();
    private RealtekCaptureState _currentCaptureState = new();
    private DriverHardwareTweaks _currentDriverTweaks = new();
    private List<PropertyEntryInfo> _allPropertyEntries = new();
    private AppUserSettings _appSettings = new();

    // Master Pre-Mix Digital Gain Knob & Auto-Gain Anti-Clip Limiter State
    private const double MinMasterGainDb = -24.0;
    private const double MaxMasterGainDb = 12.0;
    private double _userTargetGainDb;
    private double _liveEffectiveGainDb;
    private bool _positiveApoBoostApplied;
    private bool _isDraggingGainKnob;
    private Point _gainDragStartPoint;
    private double _gainDragStartDb;
    private DateTime _lastGainClickUtc = DateTime.MinValue;
    private DateTime _lastAutoGainCommitUtc = DateTime.MinValue;
    private DateTime _lastPeakAboveReleaseUtc = DateTime.MinValue;
    private float _autoGainMasterBaselineVol = -1f;
    private bool _autoGainCurrentlyLimiting;
    private int _meterTickCounter;

    private static readonly string[] EqBandRoleDescriptions =
    {
        "Sub-Bass rumble & deepest low end",
        "Bass punch, kick drums & warmth",
        "Upper bass body & fullness",
        "Lower midrange warmth & thickness",
        "Midrange body & instrument foundation",
        "Vocal clarity & core presence",
        "Vocal bite, guitars & articulation",
        "Detail, crispness & snare snap",
        "Treble sparkle & cymbal shimmer",
        "Ultra-high air, openness & brilliance"
    };

    private bool _suppressUiEvents;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayPresetsMenu;
    private HwndSource? _hwndSource;
    private bool _hotkeysRegistered;

    // Win32 Global Hotkeys
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int HOTKEY_ID_TOGGLE_EQ = 9001;
    private const int HOTKEY_ID_NEXT_PRESET = 9002;
    private const int HOTKEY_ID_TOGGLE_LOUDNESS = 9003;
    private const int HOTKEY_ID_TOGGLE_KARAOKE = 9004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();

        _appSettings = _studio.Presets.LoadAppSettings();
        _userTargetGainDb = Math.Round(Math.Clamp(_appSettings.MasterPreampGainDb, MinMasterGainDb, MaxMasterGainDb), 1);
        _liveEffectiveGainDb = _userTargetGainDb;

        // Global tooltip gate so toggling "Show Helpful Tooltips" disables/enables every tooltip across all tabs instantly
        AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnAnyToolTipOpening), handledEventsToo: true);

        BuildEqualizerBandSliders();
        BuildRoomCorrectionChannelRows();
        PopulateEnvironmentPresets();
        InitializeSystemTray();
        InitializeVisualizer();
        ApplyAppSettingsToHeaderUi();

        _meterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _meterTimer.Tick += MeterTimer_Tick;

        _jackSenseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _jackSenseTimer.Tick += JackSenseTimer_Tick;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;

        if (startMinimized)
        {
            // Ensure data and presets initialize immediately even when starting hidden in tray
            RefreshPresetComboBox();
            RefreshAllData();
        }
    }

    private void OnAnyToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (!_appSettings.TooltipsEnabled)
        {
            e.Handled = true;
        }
    }

    private static Version GetCurrentAppVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 1);
    }

    private void ApplyAppSettingsToHeaderUi()
    {
        bool prev = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            MnuShowTooltips.IsChecked = _appSettings.TooltipsEnabled;
            BtnAutoGain.IsChecked = _appSettings.AutoGainEnabled;
            BtnAutoGain.Content = $"Auto {_appSettings.AutoGainCeilingPercent}%";

            MnuCeiling70.IsChecked = _appSettings.AutoGainCeilingPercent == 70;
            MnuCeiling75.IsChecked = _appSettings.AutoGainCeilingPercent == 75;
            MnuCeiling80.IsChecked = _appSettings.AutoGainCeilingPercent == 80;
            MnuCeiling85.IsChecked = _appSettings.AutoGainCeilingPercent == 85;
            MnuCeiling90.IsChecked = _appSettings.AutoGainCeilingPercent == 90;
            MnuCeiling95.IsChecked = _appSettings.AutoGainCeilingPercent == 95;

            bool isMasterVolMode = string.Equals(_appSettings.AutoGainMode, "MasterVolume", StringComparison.OrdinalIgnoreCase);
            MnuModePreamp.IsChecked = !isMasterVolMode;
            MnuModeMasterVol.IsChecked = isMasterVolMode;

            MnuAboutApp.Header = $"About Spectra Realtek Studio ({UpdateCheckService.FormatVersion(GetCurrentAppVersion())})";
            UpdateGainKnobVisual();
            UpdateMeterCeilingMarkers();
        }
        finally
        {
            _suppressUiEvents = prev;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshPresetComboBox();
        RefreshAllData();
        UpdateGainKnobVisual();
        UpdateMeterCeilingMarkers();
        _meterTimer.Start();
        _jackSenseTimer.Start();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        if (ChkGlobalHotkeys.IsChecked == true)
        {
            RegisterGlobalHotkeys();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            StopVisualizer();
        }
        else if (_visIsActiveTab && IsVisible && !_visPaused)
        {
            StartVisualizer();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _meterTimer.Stop();
        _jackSenseTimer.Stop();
        StopVisualizer();
        _studio.CoreAudio.RestoreAllPreMixSessionGains(_activePlaybackEndpoint?.EndpointId);
        UnregisterGlobalHotkeys();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    // =========================================================================
    // DYNAMIC UI BUILDERS (EQ SLIDERS, ROOM CORRECTION, SYSTEM TRAY)
    // =========================================================================

    private void BuildEqualizerBandSliders()
    {
        GridEqSliders.Children.Clear();
        for (int i = 0; i < 10; i++)
        {
            int bandIndex = i;
            var colGrid = new Grid
            {
                Margin = new Thickness(4, 0, 4, 0)
            };
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var valText = new TextBlock
            {
                Text = "0.0 dB",
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(valText, 0);
            _eqBandValueLabels[i] = valText;

            var slider = new Slider
            {
                Style = (Style)FindResource("SpectraEqSliderStyle"),
                Orientation = Orientation.Vertical,
                Minimum = -12.0,
                Maximum = 12.0,
                Value = 0.0,
                SmallChange = 0.1,
                LargeChange = 1.0,
                TickFrequency = 0.1,
                IsSnapToTickEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = $"{RealtekPropertyKeys.EqBandLabels[i]} — {EqBandRoleDescriptions[i]} (-12.0 to +12.0 dB • Double-click to reset to 0.0 dB)"
            };
            slider.ValueChanged += (_, _) => OnEqBandSliderChanged(bandIndex);
            slider.MouseDoubleClick += (_, _) =>
            {
                slider.Value = 0.0;
            };
            Grid.SetRow(slider, 1);
            _eqBandSliders[i] = slider;

            var freqText = new TextBlock
            {
                Text = RealtekPropertyKeys.EqBandLabels[i],
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(freqText, 2);

            colGrid.Children.Add(valText);
            colGrid.Children.Add(slider);
            colGrid.Children.Add(freqText);
            GridEqSliders.Children.Add(colGrid);
        }
    }

    private void BuildRoomCorrectionChannelRows()
    {
        PanelRoomCorrectionChannels.Children.Clear();
        for (int i = 0; i < 8; i++)
        {
            int chIdx = i;
            var border = new Border
            {
                Background = (Brush)FindResource("PanelAlt"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

            var nameTxt = new TextBlock
            {
                Text = RoomSpeakerNames[i],
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameTxt, 0);

            var distTitle = new TextBlock
            {
                Text = "Dist:",
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            };
            Grid.SetColumn(distTitle, 1);

            var sldDist = new Slider
            {
                Minimum = 0.0,
                Maximum = 10.0,
                Value = 0.0,
                SmallChange = 0.1,
                LargeChange = 0.5,
                TickFrequency = 0.1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Distance from listening position to {RoomSpeakerNames[i]} in meters (0.0–10.0 m) for time-alignment delay"
            };
            Grid.SetColumn(sldDist, 2);
            _rcDistanceSliders[i] = sldDist;

            var lblDist = new TextBlock
            {
                Text = "0.00 m",
                Foreground = (Brush)FindResource("Cyan"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            };
            Grid.SetColumn(lblDist, 3);
            _rcDistanceLabels[i] = lblDist;

            sldDist.ValueChanged += (_, _) =>
            {
                _rcDistanceLabels[chIdx].Text = $"{sldDist.Value:0.00} m";
            };

            var gainTitle = new TextBlock
            {
                Text = "Trim:",
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            };
            Grid.SetColumn(gainTitle, 4);

            var sldGain = new Slider
            {
                Minimum = -12,
                Maximum = 12,
                Value = 0,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Per-channel gain trim for {RoomSpeakerNames[i]} (-12 to +12 dB) to balance uneven speaker volumes"
            };
            Grid.SetColumn(sldGain, 5);
            _rcGainSliders[i] = sldGain;

            var lblGain = new TextBlock
            {
                Text = "+0 dB",
                Foreground = (Brush)FindResource("Magenta"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lblGain, 6);
            _rcGainLabels[i] = lblGain;

            sldGain.ValueChanged += (_, _) =>
            {
                int g = (int)Math.Round(sldGain.Value);
                _rcGainLabels[chIdx].Text = $"{g:+0;-0;0} dB";
            };

            row.Children.Add(nameTxt);
            row.Children.Add(distTitle);
            row.Children.Add(sldDist);
            row.Children.Add(lblDist);
            row.Children.Add(gainTitle);
            row.Children.Add(sldGain);
            row.Children.Add(lblGain);

            border.Child = row;
            PanelRoomCorrectionChannels.Children.Add(border);
        }
    }

    private void PopulateEnvironmentPresets()
    {
        CmbEnvironmentPresets.ItemsSource = RealtekPropertyKeys.BuiltInEnvironments;
        CmbEnvironmentPresets.SelectedIndex = 0;
    }

    private void InitializeSystemTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Spectra Realtek Studio");
            openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
            menu.Items.Add(openItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var toggleEqItem = new System.Windows.Forms.ToolStripMenuItem("Toggle 10-Band Equalizer (Ctrl+Alt+E)");
            toggleEqItem.Click += (_, _) => Dispatcher.Invoke(ToggleEqualizerShortcut);
            menu.Items.Add(toggleEqItem);

            _trayPresetsMenu = new System.Windows.Forms.ToolStripMenuItem("Quick-Switch EQ Preset");
            menu.Items.Add(_trayPresetsMenu);

            var toggleLoudItem = new System.Windows.Forms.ToolStripMenuItem("Toggle Loudness Equalization (Ctrl+Alt+L)");
            toggleLoudItem.Click += (_, _) => Dispatcher.Invoke(ToggleLoudnessShortcut);
            menu.Items.Add(toggleLoudItem);

            var toggleKaraokeItem = new System.Windows.Forms.ToolStripMenuItem("Toggle Voice Cancellation (Ctrl+Alt+K)");
            toggleKaraokeItem.Click += (_, _) => Dispatcher.Invoke(ToggleKaraokeShortcut);
            menu.Items.Add(toggleKaraokeItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit Spectra Realtek Studio");
            exitItem.Click += (_, _) => Dispatcher.Invoke(Close);
            menu.Items.Add(exitItem);

            System.Drawing.Icon? trayIconImg = null;
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    trayIconImg = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch
            {
            }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = trayIconImg ?? System.Drawing.SystemIcons.Application,
                Text = "Spectra Realtek Studio — Full HDA & APO Control",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        catch
        {
            // Tray initialization is optional in headless environments
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        if (_visIsActiveTab && !_visPaused)
        {
            StartVisualizer();
        }
    }

    // =========================================================================
    // MASTER DATA REFRESH & ENDPOINT LOADING
    // =========================================================================

    private void RefreshPresetComboBox(string? selectName = null)
    {
        bool prevSuppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            var allPresets = _studio.Presets.GetAllEqPresets();
            CmbEqPresets.ItemsSource = allPresets;

            if (!string.IsNullOrWhiteSpace(selectName))
            {
                var match = allPresets.FirstOrDefault(p => string.Equals(p.Name, selectName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    CmbEqPresets.SelectedItem = match;
                }
            }

            if (_trayPresetsMenu != null)
            {
                _trayPresetsMenu.DropDownItems.Clear();
                foreach (var preset in allPresets)
                {
                    var item = new System.Windows.Forms.ToolStripMenuItem(preset.Name);
                    item.Click += (_, _) => Dispatcher.Invoke(() => ApplyNamedEqPreset(preset));
                    _trayPresetsMenu.DropDownItems.Add(item);
                }
            }

            RebuildEqPresetChips(allPresets, selectName);
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
        }
    }

    private void RebuildEqPresetChips(List<EqPresetDefinition> allPresets, string? activeName = null)
    {
        if (PanelEqPresetChips == null)
            return;

        PanelEqPresetChips.Children.Clear();
        var chipStyle = (Style)FindResource("PresetChipButton");
        foreach (var preset in allPresets)
        {
            string shortLabel = preset.Name.StartsWith("Flat", StringComparison.OrdinalIgnoreCase) ? "Flat" : preset.Name;
            var chip = new Button
            {
                Content = shortLabel,
                Style = chipStyle,
                DataContext = preset,
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = $"Apply '{preset.Name}' 10-band equalizer curve"
            };
            if (!string.IsNullOrWhiteSpace(activeName) &&
                string.Equals(preset.Name, activeName, StringComparison.OrdinalIgnoreCase))
            {
                chip.Tag = "ACTIVE";
            }
            chip.Click += (_, _) => ApplyNamedEqPreset(preset);
            PanelEqPresetChips.Children.Add(chip);
        }
    }

    private void HighlightActiveEqPresetChip(string matchedPresetName)
    {
        if (PanelEqPresetChips == null)
            return;

        foreach (var child in PanelEqPresetChips.Children)
        {
            if (child is Button btn && btn.DataContext is EqPresetDefinition p)
            {
                btn.Tag = string.Equals(p.Name, matchedPresetName, StringComparison.OrdinalIgnoreCase) ? "ACTIVE" : null;
            }
        }
    }

    private void RefreshAllData()
    {
        _suppressUiEvents = true;
        try
        {
            _currentDriverTweaks = _studio.DriverHardware.ReadHardwareTweaks();
            string shortChip = _currentDriverTweaks.CodecChipName.Split('(')[0].Trim();
            string fullBanner = $"{_currentDriverTweaks.CodecChipName} • {_currentDriverTweaks.MotherboardSubsysId} • Driver {_currentDriverTweaks.DriverVersion} ({_currentDriverTweaks.InfPath})";
            TxtCodecBanner.Text = $"{shortChip} • Driver {_currentDriverTweaks.DriverVersion} • {_currentDriverTweaks.MotherboardSubsysId}";
            TxtCodecBanner.ToolTip = fullBanner;

            ChkDrvMuteRearOnHp.IsChecked = _currentDriverTweaks.MuteRearWhenFrontHpPlugged;
            ChkDrvIndependentStreams.IsChecked = _currentDriverTweaks.IndependentFrontRearStreams;
            ChkDrvFrontHdaMode.IsChecked = _currentDriverTweaks.FrontPanelHdAudioMode;
            ChkDrvAntiPopPower.IsChecked = _currentDriverTweaks.AntiPopDisableIdlePowerDown;
            ChkDrvSwapCenLfe.IsChecked = _currentDriverTweaks.SwapCenterLfeInDriver;

            var allEndpoints = _studio.DiscoverEndpoints(includeInactiveRealtekPins: true);
            _lastEndpointFingerprint = ComputeEndpointFingerprint(allEndpoints);
            _playbackEndpoints = allEndpoints.Where(e => e.DataFlow == AudioEndpointFlow.Render).ToList();
            _captureEndpoints = allEndpoints.Where(e => e.DataFlow == AudioEndpointFlow.Capture).ToList();

            string? prevPlaybackId = _activePlaybackEndpoint?.EndpointId;
            string? prevCaptureId = _activeCaptureEndpoint?.EndpointId;

            CmbPlaybackEndpoints.ItemsSource = _playbackEndpoints;
            _activePlaybackEndpoint = _playbackEndpoints.FirstOrDefault(e => e.EndpointId == prevPlaybackId)
                                   ?? _playbackEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive && e.IsDefaultMultimedia)
                                   ?? _playbackEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive)
                                   ?? _playbackEndpoints.FirstOrDefault();
            CmbPlaybackEndpoints.SelectedItem = _activePlaybackEndpoint;

            CmbCaptureEndpoints.ItemsSource = _captureEndpoints;
            _activeCaptureEndpoint = _captureEndpoints.FirstOrDefault(e => e.EndpointId == prevCaptureId)
                                  ?? _captureEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive && e.IsDefaultMultimedia)
                                  ?? _captureEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive)
                                  ?? _captureEndpoints.FirstOrDefault();
            CmbCaptureEndpoints.SelectedItem = _activeCaptureEndpoint;

            BuildJackPinsPanel(allEndpoints);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        LoadSelectedPlaybackEndpoint();
        LoadSelectedCaptureEndpoint();
    }

    private void LoadSelectedPlaybackEndpoint()
    {
        if (_activePlaybackEndpoint == null)
            return;

        _suppressUiEvents = true;
        try
        {
            string id = _activePlaybackEndpoint.EndpointId;
            _currentPlaybackState = _studio.ReadPlaybackState(id);

            ChkEnableAllEnhancements.IsChecked = !_currentPlaybackState.DisableAllEnhancements;
            ChkImmediateMode.IsChecked = _currentPlaybackState.ImmediateMode;

            // Master Volume & Balance
            if (_studio.CoreAudio.TryGetEndpointVolume(id, out float masterVol, out float leftVol, out float rightVol, out bool isMuted))
            {
                SldHeaderMasterVol.Value = masterVol;
                TxtHeaderMasterVol.Text = $"{masterVol:0}%";
                ChkHeaderMute.IsChecked = isMuted;

                SldBalanceLeft.Value = leftVol;
                TxtBalanceLeft.Text = $"{leftVol:0}%";
                SldBalanceRight.Value = rightVol;
                TxtBalanceRight.Text = $"{rightVol:0}%";
            }

            // Tab 1: Equalizer (only subtract positive APO boost; negative gain runs in WASAPI pre-mix without touching EQ)
            ChkEqEnable.IsChecked = _currentPlaybackState.EqualizerEnabled;
            double apoBoostOffset = _liveEffectiveGainDb > 0.05 ? _liveEffectiveGainDb : 0.0;
            double[] displayBands = new double[10];
            for (int i = 0; i < 10; i++)
            {
                double rawDb = _currentPlaybackState.EqualizerBandsDb[i];
                double userDb = apoBoostOffset > 0.05
                    ? Math.Round(Math.Clamp(rawDb - apoBoostOffset, -12.0, 12.0), 1)
                    : rawDb;
                displayBands[i] = userDb;
                _eqBandSliders[i].Value = userDb;
                UpdateEqBandLabel(i, userDb);
            }
            UpdateMatchedPresetSelection(displayBands);
            RenderEqCurve();

            if (_liveEffectiveGainDb < -0.02)
            {
                _studio.CoreAudio.TryApplyPreMixDigitalGainDb(id, _liveEffectiveGainDb);
            }

            // Tab 2: Sound Effects, Karaoke & Spatial
            ChkEnvironmentEnable.IsChecked = _currentPlaybackState.EnvironmentEnabled;
            int envIdx = Math.Clamp(_currentPlaybackState.EnvironmentPresetIndex, 0, RealtekPropertyKeys.BuiltInEnvironments.Count - 1);
            CmbEnvironmentPresets.SelectedIndex = envIdx;

            ChkLoudnessEq.IsChecked = _currentPlaybackState.LoudnessEqualizationEnabled;
            ChkVoiceCancellation.IsChecked = _currentPlaybackState.VoiceCancellationEnabled;
            SldPitchShift.Value = _currentPlaybackState.PitchShiftSemitones;
            UpdatePitchLabel(_currentPlaybackState.PitchShiftSemitones);

            ChkVirtualSurround.IsChecked = _currentPlaybackState.VirtualSurroundEnabled;
            ChkSpeakerFill.IsChecked = _currentPlaybackState.SpeakerFillEnabled;
            ChkHeadphoneVirt.IsChecked = _currentPlaybackState.HeadphoneVirtualizationEnabled;
            ChkSpeakerExpansion.IsChecked = _currentPlaybackState.SpeakerExpansionEnabled;
            ChkBassManagement.IsChecked = _currentPlaybackState.BassManagementEnabled;
            ChkSwapCenterSub.IsChecked = _currentPlaybackState.SwapCenterSubwooferEnabled;

            ChkOmniSound.IsChecked = _currentPlaybackState.OmniSoundEnabled;
            ChkOmniHeadphone.IsChecked = _currentPlaybackState.OmniHeadphoneEnabled;
            ChkVoiceBack.IsChecked = _currentPlaybackState.VoiceBackEnabled;
            ChkBassBack.IsChecked = _currentPlaybackState.BassBackEnabled;
            ChkMp3Plus.IsChecked = _currentPlaybackState.Mp3PlusEnabled;

            // Tab 3: Format, Speakers & Room Correction
            var fmt = _studio.ReadFormatState(id);
            SelectComboItemByTag(CmbSpeakerLayout, fmt.PhysicalSpeakersMask.ToString(CultureInfo.InvariantCulture), 0);
            SelectComboItemByTag(CmbSampleRate, fmt.SampleRate.ToString(CultureInfo.InvariantCulture), 1);
            SelectComboItemByTag(CmbBitDepth, (fmt.ValidBitsPerSample <= 16 ? 16 : 24).ToString(CultureInfo.InvariantCulture), 1);

            ChkFullRangeFront.IsChecked = (fmt.FullRangeSpeakersMask & 0x3) != 0;
            ChkFullRangeSurround.IsChecked = (fmt.FullRangeSpeakersMask & 0x63C) != 0;
            ChkExclusiveAllowed.IsChecked = fmt.ExclusiveModeAllowed;
            ChkExclusivePriority.IsChecked = fmt.ExclusiveModePriority;

            ChkRoomCorrectionEnable.IsChecked = _currentPlaybackState.RoomCorrectionEnabled;
            for (int i = 0; i < 8; i++)
            {
                int rawDist = i < _currentPlaybackState.RoomCorrectionDistance.Length ? _currentPlaybackState.RoomCorrectionDistance[i] : 0;
                double distMeters = rawDist > 30 ? rawDist / 1000.0 : rawDist;
                int gain = i < _currentPlaybackState.RoomCorrectionGainDb.Length ? _currentPlaybackState.RoomCorrectionGainDb[i] : 0;
                _rcDistanceSliders[i].Value = Math.Clamp(Math.Round(distMeters, 2), 0.0, 10.0);
                _rcDistanceLabels[i].Text = $"{distMeters:0.00} m";
                _rcGainSliders[i].Value = Math.Clamp(gain, -12, 12);
                _rcGainLabels[i].Text = $"{gain:+0;-0;0} dB";
            }

            // Tab 4: Analog Mixer Subunits
            BuildAnalogSubunitsPanel(id);

            // Tab 6: Live FxProperties Inspector
            _allPropertyEntries = _studio.InspectEndpointProperties(_activePlaybackEndpoint);
            ApplyPropertyFilter();

            SetStatus($"Loaded {_activePlaybackEndpoint.FriendlyName} — EQ: {(_currentPlaybackState.EqualizerEnabled ? "ON" : "OFF")} ({_currentPlaybackState.MatchedPresetName}), Format: {fmt.FormatSummary} ({fmt.SpeakerConfigurationName}).");
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void LoadSelectedCaptureEndpoint()
    {
        if (_activeCaptureEndpoint == null)
            return;

        _suppressUiEvents = true;
        try
        {
            string id = _activeCaptureEndpoint.EndpointId;
            _currentCaptureState = _studio.ReadCaptureState(id);

            SldCaptureVol.Value = _currentCaptureState.MasterVolumePercent;
            TxtCaptureVol.Text = $"{_currentCaptureState.MasterVolumePercent:0}%";
            ChkCaptureMute.IsChecked = _currentCaptureState.MasterMuted;

            SldMicBoostDb.Minimum = _currentCaptureState.MicBoostMinDb;
            SldMicBoostDb.Maximum = _currentCaptureState.MicBoostMaxDb > 0 ? _currentCaptureState.MicBoostMaxDb : 30;
            SldMicBoostDb.TickFrequency = _currentCaptureState.MicBoostStepDb > 0 ? _currentCaptureState.MicBoostStepDb : 10;
            SldMicBoostDb.Value = _currentCaptureState.MicBoostDb;
            TxtMicBoostDb.Text = $"+{_currentCaptureState.MicBoostDb:0.0} dB";

            ChkMicNoiseSuppression.IsChecked = _currentCaptureState.NoiseSuppressionEnabled;
            ChkMicAec.IsChecked = _currentCaptureState.AcousticEchoCancellationEnabled;
            ChkMicBeamForming.IsChecked = _currentCaptureState.BeamFormingEnabled;
            ChkMicDcOffset.IsChecked = _currentCaptureState.DcOffsetCancellationEnabled;
            ChkMicMono.IsChecked = _currentCaptureState.MonoMicrophoneEnabled;
            ChkMicConference.IsChecked = _currentCaptureState.ConferenceModeEnabled;
            ChkMicKeystroke.IsChecked = _currentCaptureState.KeystrokeSuppressionEnabled;
            ChkCaptureDisableAll.IsChecked = _currentCaptureState.DisableAllEnhancements;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    // =========================================================================
    // LIVE PEAK METER, AUTO-GAIN ANTI-CLIP LIMITER & REAL-TIME VOLUME SYNC
    // =========================================================================

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_activePlaybackEndpoint == null || WindowState == WindowState.Minimized)
            return;

        _meterTickCounter++;

        if (_studio.CoreAudio.TryGetPeakMeter(_activePlaybackEndpoint.EndpointId, out _, out float lPeak, out float rPeak))
        {
            double lPct = Math.Clamp(lPeak * 100.0, 0.0, 100.0);
            double rPct = Math.Clamp(rPeak * 100.0, 0.0, 100.0);
            double maxPeakPct = Math.Max(lPct, rPct);

            PbPeakLeft.Value = lPct;
            PbPeakRight.Value = rPct;

            // Clip / Limiter visual indicator on peak bars
            if (maxPeakPct >= 96.0)
            {
                var redBrush = (Brush)FindResource("Red");
                PbPeakLeft.Foreground = redBrush;
                PbPeakRight.Foreground = redBrush;
                TxtClipBadge.Text = "CLIP";
                TxtClipBadge.Foreground = redBrush;
            }
            else if (_appSettings.AutoGainEnabled && maxPeakPct >= _appSettings.AutoGainCeilingPercent)
            {
                var amberBrush = (Brush)FindResource("Amber");
                PbPeakLeft.Foreground = amberBrush;
                PbPeakRight.Foreground = amberBrush;
                TxtClipBadge.Text = "LIM";
                TxtClipBadge.Foreground = amberBrush;
            }
            else
            {
                PbPeakLeft.Foreground = (Brush)FindResource("Cyan");
                PbPeakRight.Foreground = (Brush)FindResource("Magenta");
                TxtClipBadge.Text = maxPeakPct > 1.0 ? "OK" : "IDLE";
                TxtClipBadge.Foreground = maxPeakPct > 1.0 ? (Brush)FindResource("Green") : (Brush)FindResource("Muted");
            }

            if (_appSettings.AutoGainEnabled)
            {
                ProcessAutoGainLimiter(maxPeakPct);
            }
        }

        // Sync Master Volume, Mute, and Tab 4 Stereo Channel Balance with Windows every ~140 ms
        if ((_meterTickCounter % 4) == 0)
        {
            SyncMasterAndBalanceSlidersFromHardware();
            if (_appSettings.AutoGainEnabled)
            {
                UpdateMeterCeilingMarkers();
            }
        }

        // Catch any newly opened audio apps/browser tabs every ~500 ms when pre-mix digital headroom trim is active
        if ((_meterTickCounter % 14) == 0 && _liveEffectiveGainDb < -0.02)
        {
            _studio.CoreAudio.TryApplyPreMixDigitalGainDb(_activePlaybackEndpoint.EndpointId, _liveEffectiveGainDb);
        }
    }

    private void ProcessAutoGainLimiter(double maxPeakPct)
    {
        if (_activePlaybackEndpoint == null)
            return;

        int ceiling = Math.Clamp(_appSettings.AutoGainCeilingPercent, 60, 98);
        bool usePreMixDigitalMode = !string.Equals(_appSettings.AutoGainMode, "MasterVolume", StringComparison.OrdinalIgnoreCase);

        DateTime now = DateTime.UtcNow;

        if (maxPeakPct > ceiling)
        {
            _lastPeakAboveReleaseUtc = now;

            // Fast, glitch-free attack (80 ms for instant in-memory WASAPI Pre-Mix trim, 180 ms for Master Volume)
            int attackIntervalMs = usePreMixDigitalMode ? 80 : 180;
            if ((now - _lastAutoGainCommitUtc).TotalMilliseconds < attackIntervalMs)
                return;

            if (usePreMixDigitalMode)
            {
                double excessDb = 20.0 * Math.Log10(Math.Max(1.0, maxPeakPct) / ceiling);
                double stepDb = Math.Clamp(Math.Round(excessDb * 1.25, 1), 0.5, 4.0);
                double nextGain = Math.Round(Math.Max(MinMasterGainDb, _liveEffectiveGainDb - stepDb), 1);

                if (nextGain < _liveEffectiveGainDb - 0.05)
                {
                    _liveEffectiveGainDb = nextGain;
                    _autoGainCurrentlyLimiting = true;
                    _lastAutoGainCommitUtc = now;
                    ApplyLiveMasterGainToHardware();
                    UpdateGainKnobVisual();
                    TxtAutoLimitActive.Text = "LIM";
                }
            }
            else
            {
                // Master Volume Limiter mode
                if (_autoGainMasterBaselineVol < 0f)
                {
                    _autoGainMasterBaselineVol = (float)SldHeaderMasterVol.Value;
                }

                float currentVol = (float)SldHeaderMasterVol.Value;
                float stepPct = maxPeakPct > ceiling + 10.0 ? 3f : (maxPeakPct > ceiling + 4.0 ? 2f : 1f);
                float nextVol = Math.Max(5f, currentVol - stepPct);
                if (nextVol < currentVol - 0.1f)
                {
                    _autoGainCurrentlyLimiting = true;
                    _lastAutoGainCommitUtc = now;
                    bool muted = ChkHeaderMute.IsChecked == true;
                    _studio.CoreAudio.TrySetEndpointMasterVolume(_activePlaybackEndpoint.EndpointId, nextVol, muted);
                    SyncMasterAndBalanceSlidersFromHardware(forceUpdate: true);
                    TxtAutoLimitActive.Text = "LIM";
                }
            }
        }
        else if (_autoGainCurrentlyLimiting && maxPeakPct < ceiling - 8.0)
        {
            // Smooth gentle recovery after 1.6 seconds of clean headroom below ceiling (never boosts above user target!)
            if ((now - _lastPeakAboveReleaseUtc).TotalMilliseconds < 1600)
                return;
            if ((now - _lastAutoGainCommitUtc).TotalMilliseconds < 500)
                return;

            if (usePreMixDigitalMode)
            {
                if (_liveEffectiveGainDb < _userTargetGainDb - 0.05)
                {
                    _liveEffectiveGainDb = Math.Round(Math.Min(_userTargetGainDb, _liveEffectiveGainDb + 0.5), 1);
                    _lastAutoGainCommitUtc = now;
                    ApplyLiveMasterGainToHardware();
                    if (Math.Abs(_liveEffectiveGainDb - _userTargetGainDb) < 0.05)
                    {
                        _autoGainCurrentlyLimiting = false;
                        TxtAutoLimitActive.Text = string.Empty;
                    }
                    UpdateGainKnobVisual();
                }
                else
                {
                    _autoGainCurrentlyLimiting = false;
                    TxtAutoLimitActive.Text = string.Empty;
                    UpdateGainKnobVisual();
                }
            }
            else if (_autoGainMasterBaselineVol >= 0f)
            {
                float currentVol = (float)SldHeaderMasterVol.Value;
                if (currentVol < _autoGainMasterBaselineVol - 0.4f)
                {
                    float nextVol = Math.Min(_autoGainMasterBaselineVol, currentVol + 1f);
                    _lastAutoGainCommitUtc = now;
                    bool muted = ChkHeaderMute.IsChecked == true;
                    _studio.CoreAudio.TrySetEndpointMasterVolume(_activePlaybackEndpoint.EndpointId, nextVol, muted);
                    SyncMasterAndBalanceSlidersFromHardware(forceUpdate: true);
                    if (nextVol >= _autoGainMasterBaselineVol - 0.4f)
                    {
                        _autoGainCurrentlyLimiting = false;
                        TxtAutoLimitActive.Text = string.Empty;
                    }
                }
                else
                {
                    _autoGainCurrentlyLimiting = false;
                    TxtAutoLimitActive.Text = string.Empty;
                }
            }
        }
    }

    private void SyncMasterAndBalanceSlidersFromHardware(bool forceUpdate = false)
    {
        if (_activePlaybackEndpoint == null)
            return;

        if (!_studio.CoreAudio.TryGetEndpointVolume(_activePlaybackEndpoint.EndpointId, out float masterVol, out float leftVol, out float rightVol, out bool isMuted))
            return;

        bool prevSuppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            if (forceUpdate || !SldHeaderMasterVol.IsMouseCaptureWithin)
            {
                if (Math.Abs(SldHeaderMasterVol.Value - masterVol) >= 0.4)
                {
                    SldHeaderMasterVol.Value = masterVol;
                    TxtHeaderMasterVol.Text = $"{masterVol:0}%";
                }
                if ((ChkHeaderMute.IsChecked == true) != isMuted)
                {
                    ChkHeaderMute.IsChecked = isMuted;
                }
            }

            if (forceUpdate || !SldBalanceLeft.IsMouseCaptureWithin)
            {
                if (Math.Abs(SldBalanceLeft.Value - leftVol) >= 0.4)
                {
                    SldBalanceLeft.Value = leftVol;
                    TxtBalanceLeft.Text = $"{leftVol:0}%";
                }
            }

            if (forceUpdate || !SldBalanceRight.IsMouseCaptureWithin)
            {
                if (Math.Abs(SldBalanceRight.Value - rightVol) >= 0.4)
                {
                    SldBalanceRight.Value = rightVol;
                    TxtBalanceRight.Text = $"{rightVol:0}%";
                }
            }
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
        }
    }

    // =========================================================================
    // HEADER & ENDPOINT BAR EVENT HANDLERS + ROTARY GAIN KNOB & SETTINGS MENU
    // =========================================================================

    private void SldHeaderMasterVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        float vol = (float)SldHeaderMasterVol.Value;
        bool muted = ChkHeaderMute.IsChecked == true;
        TxtHeaderMasterVol.Text = $"{vol:0}%";
        _studio.CoreAudio.TrySetEndpointMasterVolume(_activePlaybackEndpoint.EndpointId, vol, muted);

        // User manually adjusted Master Volume -> update Auto-Gain baseline and immediately sync Tab 4 L/R Balance
        if (SldHeaderMasterVol.IsMouseCaptureWithin || !_autoGainCurrentlyLimiting)
        {
            _autoGainMasterBaselineVol = vol;
        }
        SyncMasterAndBalanceSlidersFromHardware(forceUpdate: false);
    }

    private void ChkHeaderMute_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        float vol = (float)SldHeaderMasterVol.Value;
        bool muted = ChkHeaderMute.IsChecked == true;
        _studio.CoreAudio.TrySetEndpointMasterVolume(_activePlaybackEndpoint.EndpointId, vol, muted);
        SetStatus($"Master Output {(muted ? "Muted" : $"Unmuted ({vol:0}%)")}.");
    }

    private void GainKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastGainClickUtc).TotalMilliseconds < 320)
        {
            // Double-click resets Gain cleanly to 0.0 dB
            _lastGainClickUtc = DateTime.MinValue;
            SetMasterPreampGainDb(0.0, persist: true);
            SetStatus("Master Preamp Gain reset to 0.0 dB.");
            return;
        }
        _lastGainClickUtc = now;

        _isDraggingGainKnob = true;
        _gainDragStartPoint = e.GetPosition(this);
        _gainDragStartDb = _userTargetGainDb;
        GainKnobControl.CaptureMouse();
        e.Handled = true;
    }

    private void GainKnob_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingGainKnob)
            return;

        Point pt = e.GetPosition(this);
        double deltaPx = (_gainDragStartPoint.Y - pt.Y) + (pt.X - _gainDragStartPoint.X) * 0.5;
        double deltaDb = Math.Round((deltaPx / 4.5) * 2.0) / 2.0; // Responsive 0.5 dB steps across -24..+12 dB
        double newDb = Math.Clamp(_gainDragStartDb + deltaDb, MinMasterGainDb, MaxMasterGainDb);
        if (Math.Abs(newDb - _userTargetGainDb) >= 0.45)
        {
            SetMasterPreampGainDb(newDb, persist: false);
        }
    }

    private void GainKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingGainKnob)
            return;

        _isDraggingGainKnob = false;
        GainKnobControl.ReleaseMouseCapture();
        _appSettings.MasterPreampGainDb = _userTargetGainDb;
        _studio.Presets.SaveAppSettings(_appSettings);
        e.Handled = true;
    }

    private void GainKnob_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double inc = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1.0 : 0.5;
        double step = e.Delta > 0 ? inc : -inc;
        double newDb = Math.Clamp(Math.Round((_userTargetGainDb + step) * 2.0) / 2.0, MinMasterGainDb, MaxMasterGainDb);
        SetMasterPreampGainDb(newDb, persist: true);
        e.Handled = true;
    }

    private void GainKnob_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SetMasterPreampGainDb(0.0, persist: true);
        SetStatus("Master Pre-Mix Gain reset to 0.0 dB.");
        e.Handled = true;
    }

    private void ApplyLiveMasterGainToHardware()
    {
        if (_activePlaybackEndpoint == null)
            return;

        if (_liveEffectiveGainDb <= 0.02)
        {
            // Negative / unity gain runs directly in 32-bit float WASAPI Pre-Mix Digital Gain (pre-meter, works even with RltkAPO64 OFF!)
            _studio.CoreAudio.TryApplyPreMixDigitalGainDb(_activePlaybackEndpoint.EndpointId, _liveEffectiveGainDb);
            if (_positiveApoBoostApplied)
            {
                _positiveApoBoostApplied = false;
                CommitEqAndPreampToHardware();
            }
            else
            {
                UpdateEqPresetBadgeText(GetCurrentSliderBands());
            }
        }
        else
        {
            // Positive gain (+0.5..+12.0 dB): keep WASAPI pre-mix at unity (0 dB) and apply boost in RltkAPO64
            _studio.CoreAudio.TryApplyPreMixDigitalGainDb(_activePlaybackEndpoint.EndpointId, 0.0);
            _positiveApoBoostApplied = true;
            CommitEqAndPreampToHardware();
        }
    }

    private void SetMasterPreampGainDb(double gainDb, bool persist)
    {
        double clamped = Math.Round(Math.Clamp(gainDb, MinMasterGainDb, MaxMasterGainDb), 1);
        _userTargetGainDb = clamped;
        _liveEffectiveGainDb = clamped;
        _autoGainCurrentlyLimiting = false;
        TxtAutoLimitActive.Text = string.Empty;

        UpdateGainKnobVisual();
        ApplyLiveMasterGainToHardware();

        if (persist)
        {
            _appSettings.MasterPreampGainDb = clamped;
            _studio.Presets.SaveAppSettings(_appSettings);
        }

        if (clamped > 0.05 && ChkEnableAllEnhancements.IsChecked != true)
        {
            SetStatus($"Master Gain +{clamped:0.0} dB (Note: Positive boost requires 'Enable Realtek System Effects (RltkAPO64)' checked; negative dB anti-clip trim works in all modes!).");
        }
        else if (clamped < -0.05)
        {
            double pct = Math.Pow(10.0, clamped / 20.0) * 100.0;
            SetStatus($"Master Pre-Mix Digital Gain: {clamped:0.0} dB ({pct:0}% pre-mixer headroom — anti-clip active).");
        }
        else
        {
            SetStatus($"Master Gain set to {clamped:+0.0;-0.0;0.0} dB.");
        }
    }

    private void UpdateGainKnobVisual()
    {
        double db = _liveEffectiveGainDb;
        // Map [-12..0] -> [-135°..0°], [0..+6] -> [0°..+135°] so 0.0 dB is straight up at 12 o'clock
        double angleDeg = db <= 0.0
            ? (db / Math.Abs(MinMasterGainDb)) * 135.0
            : (db / MaxMasterGainDb) * 135.0;
        angleDeg = Math.Clamp(angleDeg, -135.0, 135.0);

        GainKnobRotate.Angle = angleDeg;
        TxtMasterGainDb.Text = $"{db:+0.0;-0.0;0.0} dB";

        Brush activeBrush;
        if (_autoGainCurrentlyLimiting)
            activeBrush = (Brush)FindResource("Amber");
        else if (db < -0.05)
            activeBrush = (Brush)FindResource("Cyan");
        else if (db > 0.05)
            activeBrush = (Brush)FindResource("Magenta");
        else
            activeBrush = (Brush)FindResource("Muted");

        TxtMasterGainDb.Foreground = Math.Abs(db) < 0.05 && !_autoGainCurrentlyLimiting
            ? (Brush)FindResource("Text")
            : activeBrush;

        // Build crisp arc from 12 o'clock (0°) to angleDeg on 34×34 knob (center=17,17 radius=15)
        if (Math.Abs(angleDeg) < 1.0)
        {
            GainKnobValueArc.Data = Geometry.Empty;
        }
        else
        {
            const double cx = 17.0, cy = 17.0, r = 15.0;
            double rad = (angleDeg - 90.0) * Math.PI / 180.0;
            var startPt = new Point(cx, cy - r);
            var endPt = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
            var sweep = angleDeg > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            var fig = new PathFigure { StartPoint = startPt, IsClosed = false };
            fig.Segments.Add(new ArcSegment(endPt, new Size(r, r), 0, Math.Abs(angleDeg) > 180.0, sweep, true));
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            GainKnobValueArc.Stroke = activeBrush;
            GainKnobValueArc.Data = geom;
        }
    }

    private void UpdateMeterCeilingMarkers()
    {
        bool show = _appSettings.AutoGainEnabled;
        Visibility vis = show ? Visibility.Visible : Visibility.Collapsed;
        MeterCeilingMarkL.Visibility = vis;
        MeterCeilingMarkR.Visibility = vis;

        if (show && PbPeakLeft.ActualWidth > 10)
        {
            double x = PbPeakLeft.ActualWidth * (_appSettings.AutoGainCeilingPercent / 100.0);
            Canvas.SetLeft(MeterCeilingMarkL, x);
            Canvas.SetLeft(MeterCeilingMarkR, x);
        }
    }

    private void BtnAutoGain_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool enabled = BtnAutoGain.IsChecked == true;
        _appSettings.AutoGainEnabled = enabled;
        _studio.Presets.SaveAppSettings(_appSettings);

        if (enabled)
        {
            _autoGainMasterBaselineVol = (float)SldHeaderMasterVol.Value;
            _autoGainCurrentlyLimiting = false;
            UpdateMeterCeilingMarkers();
            string modeName = string.Equals(_appSettings.AutoGainMode, "MasterVolume", StringComparison.OrdinalIgnoreCase)
                ? "Master Volume Limiter"
                : "Pre-Mix Digital Headroom (Anti-Clip)";
            SetStatus($"Auto-Gain Anti-Clip Limiter ENABLED (Ceiling: {_appSettings.AutoGainCeilingPercent}% • Mode: {modeName}).");
        }
        else
        {
            _autoGainCurrentlyLimiting = false;
            TxtAutoLimitActive.Text = string.Empty;
            UpdateMeterCeilingMarkers();

            // Cleanly restore user's target gain if Auto-Gain had pulled it down
            if (Math.Abs(_liveEffectiveGainDb - _userTargetGainDb) >= 0.05)
            {
                _liveEffectiveGainDb = _userTargetGainDb;
                ApplyLiveMasterGainToHardware();
                UpdateGainKnobVisual();
            }
            SetStatus("Auto-Gain Anti-Clip Limiter DISABLED.");
        }
    }

    private void BtnSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (BtnSettingsMenu.ContextMenu != null)
        {
            BtnSettingsMenu.ContextMenu.PlacementTarget = BtnSettingsMenu;
            BtnSettingsMenu.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BtnSettingsMenu.ContextMenu.IsOpen = true;
        }
    }

    private void MnuShowTooltips_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.TooltipsEnabled = MnuShowTooltips.IsChecked;
        _studio.Presets.SaveAppSettings(_appSettings);
        SetStatus($"Helpful Tooltips {(_appSettings.TooltipsEnabled ? "ENABLED" : "DISABLED")}.");
    }

    private void MnuAutoGainCeiling_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tagStr && int.TryParse(tagStr, out int pct))
        {
            _appSettings.AutoGainCeilingPercent = Math.Clamp(pct, 60, 98);
            _studio.Presets.SaveAppSettings(_appSettings);
            ApplyAppSettingsToHeaderUi();
            SetStatus($"Auto-Gain peak ceiling set to {_appSettings.AutoGainCeilingPercent}%.");
        }
    }

    private void MnuAutoGainMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string mode)
        {
            _appSettings.AutoGainMode = mode;
            _studio.Presets.SaveAppSettings(_appSettings);
            _autoGainMasterBaselineVol = (float)SldHeaderMasterVol.Value;
            ApplyAppSettingsToHeaderUi();
            SetStatus($"Auto-Gain limiter mode switched to {(mode == "MasterVolume" ? "Master Output Volume Limiter" : "Preamp Digital Headroom (RltkAPO64 Anti-Clip)")}.");
        }
    }

    private void MnuResetGain_Click(object sender, RoutedEventArgs e)
    {
        SetMasterPreampGainDb(0.0, persist: true);
        SetStatus("Master Preamp Gain reset to 0.0 dB.");
    }

    private async void MnuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        Version currentVer = GetCurrentAppVersion();
        SetStatus($"Checking GitHub Releases for updates (current: {UpdateCheckService.FormatVersion(currentVer)})...");
        MnuCheckUpdates.IsEnabled = false;
        try
        {
            var res = await _studio.Updates.CheckForUpdatesAsync(currentVer);
            if (!res.Success)
            {
                SetStatus(res.ErrorMessage);
                MessageBox.Show(
                    this,
                    $"Could not check for updates right now.\n\n{res.ErrorMessage}\n\nYou can also check manually at:\n{UpdateCheckService.RepoUrl}/releases",
                    "Check for Updates — Spectra Realtek Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (res.UpdateAvailable)
            {
                SetStatus($"Update available: {res.LatestVersion} (current: {res.CurrentVersion}).");
                string summary = string.IsNullOrWhiteSpace(res.ReleaseNotesSummary)
                    ? string.Empty
                    : $"\n\nHighlights:\n{res.ReleaseNotesSummary}";

                var answer = MessageBox.Show(
                    this,
                    $"A new version of Spectra Realtek Studio is available!\n\nInstalled: {res.CurrentVersion}\nLatest: {res.LatestVersion} ({res.ReleaseTitle}){summary}\n\nOpen the GitHub Releases page in your browser to download?",
                    "Update Available — Spectra Realtek Studio",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(res.ReleaseUrl) { UseShellExecute = true });
                }
            }
            else
            {
                SetStatus($"You are up to date! Running Spectra Realtek Studio {res.CurrentVersion} (latest release: {res.LatestVersion}).");
                MessageBox.Show(
                    this,
                    $"Spectra Realtek Studio is up to date!\n\nInstalled version: {res.CurrentVersion}\nLatest GitHub release: {res.LatestVersion}",
                    "Up to Date — Spectra Realtek Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        finally
        {
            MnuCheckUpdates.IsEnabled = true;
        }
    }

    private void MnuOpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateCheckService.RepoUrl) { UseShellExecute = true });
            SetStatus("Opened Spectra Realtek Studio GitHub repository.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open GitHub link: {ex.Message}");
        }
    }

    private void MnuAboutApp_Click(object sender, RoutedEventArgs e)
    {
        string ver = UpdateCheckService.FormatVersion(GetCurrentAppVersion());
        MessageBox.Show(
            this,
            $"Spectra Realtek Studio {ver}\nOpen-Source Realtek HDA & RltkAPO64 Control Console\n\n" +
            $"Author: Creator-NL / Biohazard Corp\nLicense: MIT Open Source\n\n" +
            $"Detected Hardware:\n{_currentDriverTweaks.CodecChipName}\n{_currentDriverTweaks.MotherboardSubsysId}\nDriver: {_currentDriverTweaks.DriverVersion}\n\n" +
            $"GitHub: {UpdateCheckService.RepoUrl}",
            "About Spectra Realtek Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CmbPlaybackEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        if (CmbPlaybackEndpoints.SelectedItem is RealtekEndpointInfo ep)
        {
            _activePlaybackEndpoint = ep;
            LoadSelectedPlaybackEndpoint();
            OnActivePlaybackEndpointChangedForVisualizer();
        }
    }

    private void BtnSetDefaultPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEndpoint == null)
            return;

        if (_studio.PolicyConfig.TrySetDefaultEndpoint(_activePlaybackEndpoint.EndpointId))
        {
            SetStatus($"Set '{_activePlaybackEndpoint.FriendlyName}' as Default Windows Playback Device.");
            RefreshAllData();
        }
        else
        {
            SetStatus($"Could not set '{_activePlaybackEndpoint.FriendlyName}' as default (endpoint may be unplugged).");
        }
    }

    private void ChkEnableAllEnhancements_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        bool enableEnhancements = ChkEnableAllEnhancements.IsChecked == true;
        uint disableVal = enableEnhancements ? 0u : 1u;
        _studio.PolicyConfig.TrySetUInt32(_activePlaybackEndpoint.EndpointId, true, RealtekPropertyKeys.DisableSysFx, disableVal);
        _studio.PolicyConfig.TrySetUInt32(_activePlaybackEndpoint.EndpointId, false, RealtekPropertyKeys.DisableSysFx, disableVal);
        SetStatus($"Realtek System Effects (RltkAPO64.dll) {(enableEnhancements ? "ENABLED" : "DISABLED (Bypassed)")}.");
    }

    private void ChkImmediateMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        bool imm = ChkImmediateMode.IsChecked == true;
        _studio.PolicyConfig.TrySetUInt32(_activePlaybackEndpoint.EndpointId, true, RealtekPropertyKeys.ImmediateMode, imm ? 2u : 0u);
        SetStatus($"Immediate Mode {(imm ? "ENABLED (Live APO updates)" : "DISABLED")}.");
    }

    private void BtnOpenWindowsSoundCpl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,0") { UseShellExecute = true });
            SetStatus("Opened Windows Sound Control Panel (mmsys.cpl).");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch mmsys.cpl: {ex.Message}");
        }
    }

    private void BtnImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Spectra Realtek Studio Profile",
            Filter = "Spectra Realtek Profile (*.json)|*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var profile = _studio.Presets.ImportProfileFromFile(dlg.FileName);
            if (profile == null)
            {
                SetStatus("Selected profile file could not be parsed.");
                return;
            }

            if (profile.PlaybackState != null && _activePlaybackEndpoint != null)
            {
                profile.PlaybackState.EndpointId = _activePlaybackEndpoint.EndpointId;
                _studio.ApplyPlaybackState(profile.PlaybackState);
            }

            if (profile.CaptureState != null && _activeCaptureEndpoint != null)
            {
                profile.CaptureState.EndpointId = _activeCaptureEndpoint.EndpointId;
                _studio.ApplyCaptureState(profile.CaptureState);
            }

            LoadSelectedPlaybackEndpoint();
            LoadSelectedCaptureEndpoint();
            SetStatus($"Imported and applied studio profile '{profile.ProfileName}' from {System.IO.Path.GetFileName(dlg.FileName)}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Profile import error: {ex.Message}");
        }
    }

    private void BtnExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Spectra Realtek Studio Profile",
            Filter = "Spectra Realtek Profile (*.json)|*.json",
            FileName = $"SpectraRealtekProfile_{DateTime.Now:yyyyMMdd_HHmm}.json"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var profile = new StudioProfileExport
            {
                ProfileName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                CreatedUtc = DateTime.UtcNow,
                CodecHardwareId = _currentDriverTweaks.CodecHardwareId,
                DriverVersion = _currentDriverTweaks.DriverVersion,
                PlaybackState = _activePlaybackEndpoint != null ? _studio.ReadPlaybackState(_activePlaybackEndpoint.EndpointId) : null,
                CaptureState = _activeCaptureEndpoint != null ? _studio.ReadCaptureState(_activeCaptureEndpoint.EndpointId) : null,
                PlaybackFormat = _activePlaybackEndpoint != null ? _studio.ReadFormatState(_activePlaybackEndpoint.EndpointId) : null
            };
            _studio.Presets.ExportProfileToFile(dlg.FileName, profile);
            SetStatus($"Exported full studio profile to {dlg.FileName}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Profile export error: {ex.Message}");
        }
    }

    private void BtnRefreshAll_Click(object sender, RoutedEventArgs e)
    {
        RefreshAllData();
    }

    private void BtnToTray_Click(object sender, RoutedEventArgs e)
    {
        StopVisualizer();
        Hide();
        _trayIcon?.ShowBalloonTip(
            2000,
            "Spectra Realtek Studio",
            "Running in system tray. Double-click the tray icon or use Ctrl+Alt+E / N / L / K hotkeys anytime.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    // =========================================================================
    // TAB 1: 10-BAND GRAPHIC EQUALIZER & FREQUENCY CURVE
    // =========================================================================

    private void UpdateEqPresetBadgeText(double[] userBands)
    {
        string matched = _studio.Presets.MatchPresetName(userBands);
        TxtActivePresetBadge.Text = Math.Abs(_liveEffectiveGainDb) >= 0.05
            ? $"Active Preset: {matched} • Master Gain: {_liveEffectiveGainDb:+0.0;-0.0;0.0} dB"
            : $"Active Preset: {matched}";
    }

    private void CommitEqAndPreampToHardware()
    {
        if (_activePlaybackEndpoint == null)
            return;

        double[] userBands = GetCurrentSliderBands();
        double positiveBoostDb = _liveEffectiveGainDb > 0.05 ? _liveEffectiveGainDb : 0.0;
        double[] effectiveBands = new double[10];
        for (int i = 0; i < 10; i++)
        {
            effectiveBands[i] = Math.Round(Math.Clamp(userBands[i] + positiveBoostDb, -12.0, 12.0), 1);
        }

        bool userEqOn = ChkEqEnable.IsChecked == true;
        bool effectiveEqOn = userEqOn || positiveBoostDb > 0.05;
        _studio.ApplyEqualizer(_activePlaybackEndpoint.EndpointId, effectiveEqOn, effectiveBands);
        UpdateEqPresetBadgeText(userBands);
    }

    private void OnEqBandSliderChanged(int bandIndex)
    {
        double db = Math.Round(_eqBandSliders[bandIndex].Value, 1);
        UpdateEqBandLabel(bandIndex, db);

        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        double[] bands = GetCurrentSliderBands();
        if (ChkEqEnable.IsChecked != true)
        {
            _suppressUiEvents = true;
            ChkEqEnable.IsChecked = true;
            _suppressUiEvents = false;
        }

        CommitEqAndPreampToHardware();
        UpdateMatchedPresetSelection(bands);
        RenderEqCurve();
        SetStatus($"EQ Band {RealtekPropertyKeys.EqBandLabels[bandIndex]} set to {db:+0.0;-0.0;0.0} dB.");
    }

    private void UpdateEqBandLabel(int bandIndex, double db)
    {
        var lbl = _eqBandValueLabels[bandIndex];
        lbl.Text = $"{db:+0.0;-0.0;0.0} dB";
        if (db > 0.05)
            lbl.Foreground = (Brush)FindResource("Cyan");
        else if (db < -0.05)
            lbl.Foreground = (Brush)FindResource("Magenta");
        else
            lbl.Foreground = (Brush)FindResource("Muted");
    }

    private double[] GetCurrentSliderBands()
    {
        double[] bands = new double[10];
        for (int i = 0; i < 10; i++)
        {
            bands[i] = Math.Round(_eqBandSliders[i].Value, 1);
        }
        return bands;
    }

    private void UpdateMatchedPresetSelection(double[] bands)
    {
        string matched = _studio.Presets.MatchPresetName(bands);
        TxtActivePresetBadge.Text = Math.Abs(_liveEffectiveGainDb) >= 0.05
            ? $"Active Preset: {matched} • Master Gain: {_liveEffectiveGainDb:+0.0;-0.0;0.0} dB"
            : $"Active Preset: {matched}";
        HighlightActiveEqPresetChip(matched);

        bool prev = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            if (CmbEqPresets.ItemsSource is IEnumerable<EqPresetDefinition> list)
            {
                var found = list.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.OrdinalIgnoreCase));
                CmbEqPresets.SelectedItem = found;
            }
        }
        finally
        {
            _suppressUiEvents = prev;
        }
    }

    private void ChkEqEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        bool enabled = ChkEqEnable.IsChecked == true;
        CommitEqAndPreampToHardware();
        RenderEqCurve();
        SetStatus($"10-Band Graphic Equalizer {(enabled ? "ENABLED" : "DISABLED")}.");
    }

    private void CmbEqPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || CmbEqPresets.SelectedItem is not EqPresetDefinition preset)
            return;

        ApplyNamedEqPreset(preset);
    }

    private void ApplyNamedEqPreset(EqPresetDefinition preset)
    {
        if (_activePlaybackEndpoint == null)
            return;

        _suppressUiEvents = true;
        try
        {
            ChkEqEnable.IsChecked = true;
            for (int i = 0; i < 10; i++)
            {
                double db = i < preset.BandsDb.Length ? preset.BandsDb[i] : 0.0;
                _eqBandSliders[i].Value = db;
                UpdateEqBandLabel(i, db);
            }
            TxtActivePresetBadge.Text = Math.Abs(_liveEffectiveGainDb) >= 0.05
                ? $"Active Preset: {preset.Name} • Master Gain: {_liveEffectiveGainDb:+0.0;-0.0;0.0} dB"
                : $"Active Preset: {preset.Name}";
            TxtCustomPresetName.Text = preset.IsBuiltIn ? string.Empty : preset.Name;
            HighlightActiveEqPresetChip(preset.Name);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        CommitEqAndPreampToHardware();
        RenderEqCurve();
        SetStatus($"Applied EQ Preset '{preset.Name}' to {_activePlaybackEndpoint.FriendlyName}.");
    }

    private void BtnResetEqFlat_Click(object sender, RoutedEventArgs e)
    {
        var flat = RealtekPropertyKeys.BuiltInEqPresets[0];
        ApplyNamedEqPreset(flat);
        RefreshPresetComboBox("Flat");
    }

    private void BtnSaveCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtCustomPresetName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Enter a name in the Custom Preset box before clicking Save Custom.");
            TxtCustomPresetName.Focus();
            return;
        }

        try
        {
            double[] bands = GetCurrentSliderBands();
            _studio.Presets.SaveCustomEqPreset(name, bands);
            RefreshPresetComboBox(name);
            TxtActivePresetBadge.Text = $"Active Preset: {name}";
            SetStatus($"Saved custom EQ preset '{name}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save custom preset: {ex.Message}");
        }
    }

    private void BtnDeleteCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        string? name = (CmbEqPresets.SelectedItem as EqPresetDefinition)?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = TxtCustomPresetName.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Select a custom preset to delete.");
            return;
        }

        if (RealtekPropertyKeys.BuiltInEqPresets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"'{name}' is a built-in Realtek preset and cannot be deleted.");
            return;
        }

        if (_studio.Presets.DeleteCustomEqPreset(name))
        {
            TxtCustomPresetName.Text = string.Empty;
            RefreshPresetComboBox();
            UpdateMatchedPresetSelection(GetCurrentSliderBands());
            SetStatus($"Deleted custom EQ preset '{name}'.");
        }
    }

    private async void BtnTestEqTone_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Playing full-spectrum harmonic chord to test 10-Band Equalizer & EAX Reverb...");
        await _studio.TestTones.PlayChannelTestToneAsync(SpeakerTestChannel.AllStereoSweep);
    }

    private void CanvasEqCurve_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderEqCurve();
    }

    private void RenderEqCurve()
    {
        double w = CanvasEqCurve.ActualWidth;
        double h = CanvasEqCurve.ActualHeight;
        if (w < 40 || h < 30)
            return;

        CanvasEqCurve.Children.Clear();

        double padX = 36;
        double padTop = 26;
        double padBottom = 16;
        double plotW = Math.Max(10, w - padX * 2);
        double plotH = Math.Max(10, h - padTop - padBottom);
        double midY = padTop + plotH / 2.0;

        // Horizontal dB grid lines (+12, +6, 0, -6, -12)
        double[] gridDbs = { 12, 6, 0, -6, -12 };
        foreach (double gDb in gridDbs)
        {
            double y = midY - (gDb / 12.0) * (plotH / 2.0);
            var line = new Line
            {
                X1 = padX,
                X2 = w - padX,
                Y1 = y,
                Y2 = y,
                Stroke = gDb == 0
                    ? new SolidColorBrush(Color.FromRgb(48, 65, 115))
                    : new SolidColorBrush(Color.FromRgb(24, 32, 60)),
                StrokeThickness = gDb == 0 ? 1.5 : 1.0
            };
            CanvasEqCurve.Children.Add(line);
        }

        double[] bands = GetCurrentSliderBands();
        bool eqOn = ChkEqEnable.IsChecked == true;
        var points = new Point[10];
        for (int i = 0; i < 10; i++)
        {
            double x = padX + (plotW * i / 9.0);
            double effectiveDb = eqOn ? bands[i] : 0.0;
            double y = midY - (effectiveDb / 12.0) * (plotH / 2.0);
            points[i] = new Point(x, y);

            // Vertical band guide line
            var vLine = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = padTop,
                Y2 = padTop + plotH,
                Stroke = new SolidColorBrush(Color.FromRgb(20, 27, 50)),
                StrokeThickness = 1.0
            };
            CanvasEqCurve.Children.Add(vLine);
        }

        // Build smooth Catmull-Rom spline points
        var splinePoints = new PointCollection();
        for (int i = 0; i < points.Length - 1; i++)
        {
            Point p0 = i == 0 ? points[0] : points[i - 1];
            Point p1 = points[i];
            Point p2 = points[i + 1];
            Point p3 = (i + 2 < points.Length) ? points[i + 2] : points[i + 1];

            int steps = 12;
            for (int s = 0; s < steps; s++)
            {
                double t = (double)s / steps;
                double t2 = t * t;
                double t3 = t2 * t;

                double x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                double y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                y = Math.Clamp(y, padTop - 4, padTop + plotH + 4);
                splinePoints.Add(new Point(x, y));
            }
        }
        splinePoints.Add(points[^1]);

        // Filled gradient polygon under curve (matches Webpage EQ Lab Cyan -> Magenta translucent fade)
        var fillGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        if (eqOn)
        {
            fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(92, 34, 232, 255), 0.0));
            fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(40, 140, 110, 255), 0.55));
            fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 43, 214), 1.0));
        }
        else
        {
            fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(40, 127, 139, 176), 0.0));
            fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(5, 127, 139, 176), 1.0));
        }

        var fillPoly = new Polygon { Fill = fillGrad };
        var fillPts = new PointCollection { new Point(points[0].X, midY) };
        foreach (var pt in splinePoints)
            fillPts.Add(pt);
        fillPts.Add(new Point(points[^1].X, midY));
        fillPoly.Points = fillPts;
        CanvasEqCurve.Children.Add(fillPoly);

        // Main frequency response curve line (smooth horizontal Cyan -> Violet -> Magenta gradient!)
        Brush lineBrush;
        if (eqOn)
        {
            var lineGrad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            lineGrad.GradientStops.Add(new GradientStop(Color.FromRgb(34, 232, 255), 0.0));
            lineGrad.GradientStops.Add(new GradientStop(Color.FromRgb(126, 122, 255), 0.52));
            lineGrad.GradientStops.Add(new GradientStop(Color.FromRgb(255, 43, 214), 1.0));
            lineBrush = lineGrad;
        }
        else
        {
            lineBrush = (Brush)FindResource("Muted");
        }

        var curveLine = new Polyline
        {
            Points = splinePoints,
            Stroke = lineBrush,
            StrokeThickness = 3.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        CanvasEqCurve.Children.Add(curveLine);

        // 10 Band node circles (glowing Cyan for >= 0 dB, Magenta for < 0 dB)
        for (int i = 0; i < 10; i++)
        {
            var dot = new Ellipse
            {
                Width = 9.5,
                Height = 9.5,
                Fill = bands[i] >= 0 ? (Brush)FindResource("Cyan") : (Brush)FindResource("Magenta"),
                Stroke = new SolidColorBrush(Color.FromRgb(7, 10, 18)),
                StrokeThickness = 1.6
            };
            Canvas.SetLeft(dot, points[i].X - 4.75);
            Canvas.SetTop(dot, points[i].Y - 4.75);
            CanvasEqCurve.Children.Add(dot);
        }

        UpdateEqBlobHexDisplay(bands);
    }

    private void UpdateEqBlobHexDisplay(double[] userBands)
    {
        if (TxtEqBlobHex == null)
            return;

        double positiveBoostDb = _liveEffectiveGainDb > 0.05 ? _liveEffectiveGainDb : 0.0;
        var chunks = new string[10];
        for (int i = 0; i < 10; i++)
        {
            double eff = Math.Clamp(userBands[i] + positiveBoostDb, -12.0, 12.0);
            int raw = (int)Math.Round(eff * 100.0);
            byte[] le = BitConverter.GetBytes(raw);
            chunks[i] = $"{le[0]:X2} {le[1]:X2} {le[2]:X2} {le[3]:X2}";
        }
        TxtEqBlobHex.Text = string.Join(" | ", chunks);
    }

    // =========================================================================
    // TAB 2: SOUND EFFECTS, EAX ENVIRONMENT, KARAOKE & SPATIAL STUDIO
    // =========================================================================

    private void ChkEnvironmentEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        bool enabled = ChkEnvironmentEnable.IsChecked == true;
        int envIdx = Math.Max(0, CmbEnvironmentPresets.SelectedIndex);
        if (enabled && envIdx == 0)
        {
            _suppressUiEvents = true;
            envIdx = 1; // Generic
            CmbEnvironmentPresets.SelectedIndex = 1;
            _suppressUiEvents = false;
        }

        _studio.ApplyEnvironmentReverb(_activePlaybackEndpoint.EndpointId, enabled, envIdx);
        string envName = RealtekPropertyKeys.BuiltInEnvironments[envIdx].Name;
        SetStatus($"EAX Acoustic Environment {(enabled ? $"ENABLED ({envName})" : "DISABLED")}.");
    }

    private void CmbEnvironmentPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        int envIdx = Math.Max(0, CmbEnvironmentPresets.SelectedIndex);
        bool enable = envIdx > 0;

        _suppressUiEvents = true;
        ChkEnvironmentEnable.IsChecked = enable;
        _suppressUiEvents = false;

        _studio.ApplyEnvironmentReverb(_activePlaybackEndpoint.EndpointId, enable, envIdx);
        string envName = RealtekPropertyKeys.BuiltInEnvironments[envIdx].Name;
        SetStatus($"Selected Acoustic Environment: {envName}.");
    }

    private void BtnQuickEnv_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int envIdx))
        {
            CmbEnvironmentPresets.SelectedIndex = Math.Clamp(envIdx, 0, RealtekPropertyKeys.BuiltInEnvironments.Count - 1);
        }
    }

    private void PlaybackEffectToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        var state = BuildPlaybackStateFromUi();
        _studio.ApplyPlaybackState(state);

        if (sender is CheckBox chk)
        {
            SetStatus($"{chk.Content} => {(chk.IsChecked == true ? "ON" : "OFF")}");
        }
    }

    private void SldPitchShift_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int semitones = (int)Math.Round(SldPitchShift.Value);
        UpdatePitchLabel(semitones);

        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        _studio.PolicyConfig.TrySetInt32(_activePlaybackEndpoint.EndpointId, true, RealtekPropertyKeys.PitchShiftKey, semitones);
        SetStatus($"Karaoke Pitch Shift set to {semitones:+0;-0;0} Semitones.");
    }

    private void BtnResetPitch_Click(object sender, RoutedEventArgs e)
    {
        SldPitchShift.Value = 0;
    }

    private void UpdatePitchLabel(int semitones)
    {
        TxtPitchShiftVal.Text = semitones == 0
            ? "0 Semitones (Original Key)"
            : $"{semitones:+0;-0;0} Semitones";
    }

    private RealtekPlaybackState BuildPlaybackStateFromUi()
    {
        var st = new RealtekPlaybackState
        {
            EndpointId = _activePlaybackEndpoint?.EndpointId ?? string.Empty,
            DisableAllEnhancements = ChkEnableAllEnhancements.IsChecked != true,
            ImmediateMode = ChkImmediateMode.IsChecked == true,
            EqualizerEnabled = ChkEqEnable.IsChecked == true,
            EqualizerBandsDb = GetCurrentSliderBands(),
            EnvironmentEnabled = ChkEnvironmentEnable.IsChecked == true,
            EnvironmentPresetIndex = Math.Max(0, CmbEnvironmentPresets.SelectedIndex),
            VoiceCancellationEnabled = ChkVoiceCancellation.IsChecked == true,
            PitchShiftSemitones = (int)Math.Round(SldPitchShift.Value),
            LoudnessEqualizationEnabled = ChkLoudnessEq.IsChecked == true,
            SpeakerFillEnabled = ChkSpeakerFill.IsChecked == true,
            VirtualSurroundEnabled = ChkVirtualSurround.IsChecked == true,
            HeadphoneVirtualizationEnabled = ChkHeadphoneVirt.IsChecked == true,
            BassManagementEnabled = ChkBassManagement.IsChecked == true,
            SpeakerExpansionEnabled = ChkSpeakerExpansion.IsChecked == true,
            SwapCenterSubwooferEnabled = ChkSwapCenterSub.IsChecked == true,
            RoomCorrectionEnabled = ChkRoomCorrectionEnable.IsChecked == true,
            OmniSoundEnabled = ChkOmniSound.IsChecked == true,
            OmniHeadphoneEnabled = ChkOmniHeadphone.IsChecked == true,
            VoiceBackEnabled = ChkVoiceBack.IsChecked == true,
            BassBackEnabled = ChkBassBack.IsChecked == true,
            Mp3PlusEnabled = ChkMp3Plus.IsChecked == true
        };

        for (int i = 0; i < 8; i++)
        {
            st.RoomCorrectionDistance[i] = (int)Math.Round(_rcDistanceSliders[i].Value * 1000.0);
            st.RoomCorrectionGainDb[i] = (int)Math.Round(_rcGainSliders[i].Value);
        }

        return st;
    }

    // =========================================================================
    // TAB 3: SPEAKER CONFIG, FORMAT, CHANNEL TEST & ROOM CORRECTION
    // =========================================================================

    private void BtnApplyFormatAndSpeakers_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEndpoint == null)
            return;

        uint mask = GetSelectedTagUInt(CmbSpeakerLayout, 0x3u);
        int sampleRate = (int)GetSelectedTagUInt(CmbSampleRate, 48000u);
        int bitDepth = (int)GetSelectedTagUInt(CmbBitDepth, 24u);

        uint fullRangeMask = 0;
        if (ChkFullRangeFront.IsChecked == true)
            fullRangeMask |= 0x3u;
        if (ChkFullRangeSurround.IsChecked == true)
            fullRangeMask |= (mask & ~0x7u); // Surround channels excluding Front L/R and LFE

        var fmt = new AudioFormatState
        {
            EndpointId = _activePlaybackEndpoint.EndpointId,
            PhysicalSpeakersMask = mask,
            ChannelMask = mask,
            FullRangeSpeakersMask = fullRangeMask,
            SampleRate = sampleRate,
            ValidBitsPerSample = bitDepth,
            BitsPerSample = bitDepth > 16 ? 32 : 16,
            ExclusiveModeAllowed = ChkExclusiveAllowed.IsChecked == true,
            ExclusiveModePriority = ChkExclusivePriority.IsChecked == true
        };

        bool ok = _studio.ApplyFormatAndSpeakerConfig(fmt);
        SetStatus(ok
            ? $"Applied Speaker Config ({fmt.SpeakerConfigurationName}) & Format ({bitDepth}-bit, {sampleRate:N0} Hz)."
            : $"Applied Speaker Properties ({fmt.SpeakerConfigurationName}); note: shared format change may require closing active audio streams.");
    }

    private async void BtnTestSpeakerChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int chIdx))
        {
            var channel = (SpeakerTestChannel)Math.Clamp(chIdx, 0, 8);
            SetStatus($"Playing test tone on {btn.Content}...");
            await _studio.TestTones.PlayChannelTestToneAsync(channel);
        }
    }

    private void ChkRoomCorrectionEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        BtnApplyRoomCorrection_Click(sender, e);
    }

    private void BtnApplyRoomCorrection_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEndpoint == null)
            return;

        bool enabled = ChkRoomCorrectionEnable.IsChecked == true;
        int[] dists = new int[8];
        int[] gains = new int[8];
        for (int i = 0; i < 8; i++)
        {
            dists[i] = (int)Math.Round(_rcDistanceSliders[i].Value * 1000.0);
            gains[i] = (int)Math.Round(_rcGainSliders[i].Value);
        }

        _studio.PolicyConfig.TrySetUInt32(_activePlaybackEndpoint.EndpointId, true, RealtekPropertyKeys.RoomCorrectionEnable, enabled ? 1u : 0u);
        _studio.RealtekCom.TrySetRoomCorrection(_activePlaybackEndpoint.EndpointId, enabled, false, gains, dists);
        SetStatus($"Applied Room Correction ({(enabled ? "ENABLED" : "DISABLED")}) across 8 speaker channels.");
    }

    private void BtnResetRoomCorrection_Click(object sender, RoutedEventArgs e)
    {
        _suppressUiEvents = true;
        for (int i = 0; i < 8; i++)
        {
            _rcDistanceSliders[i].Value = 0.0;
            _rcDistanceLabels[i].Text = "0.00 m";
            _rcGainSliders[i].Value = 0;
            _rcGainLabels[i].Text = "+0 dB";
        }
        _suppressUiEvents = false;
        BtnApplyRoomCorrection_Click(sender, e);
    }

    // =========================================================================
    // TAB 4: ANALOG MIXER, BALANCE & DIRECT INPUT MONITORING
    // =========================================================================

    private void SldBalance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        float left = (float)SldBalanceLeft.Value;
        float right = (float)SldBalanceRight.Value;
        TxtBalanceLeft.Text = $"{left:0}%";
        TxtBalanceRight.Text = $"{right:0}%";
        _studio.CoreAudio.TrySetEndpointChannelBalance(_activePlaybackEndpoint.EndpointId, left, right);
        SyncMasterAndBalanceSlidersFromHardware(forceUpdate: false);
    }

    private void BtnCenterBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEndpoint == null)
            return;

        float max = (float)Math.Max(SldBalanceLeft.Value, SldBalanceRight.Value);
        if (max < 10f) max = 100f;

        _suppressUiEvents = true;
        SldBalanceLeft.Value = max;
        SldBalanceRight.Value = max;
        TxtBalanceLeft.Text = $"{max:0}%";
        TxtBalanceRight.Text = $"{max:0}%";
        _suppressUiEvents = false;

        _studio.CoreAudio.TrySetEndpointChannelBalance(_activePlaybackEndpoint.EndpointId, max, max);
        SyncMasterAndBalanceSlidersFromHardware(forceUpdate: true);
        SetStatus($"Equalized Left / Right Stereo Balance at {max:0}%.");
    }

    private void BuildAnalogSubunitsPanel(string endpointId)
    {
        PanelAnalogSubunits.Children.Clear();
        var subunits = _studio.GetAnalogMixerSubunits(endpointId);

        if (subunits.Count == 0)
        {
            PanelAnalogSubunits.Children.Add(new TextBlock
            {
                Text = "No analog mixer subunits found for this endpoint.",
                Foreground = (Brush)FindResource("Muted")
            });
            return;
        }

        foreach (var sub in subunits)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("PanelAlt"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(305) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = sub.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = $"Hardware passthrough ({sub.MinDb:0.0} to {sub.MaxDb:+0.0;-0.0;0.0} dB)",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Muted")
            });
            Grid.SetColumn(titleStack, 0);

            var sld = new Slider
            {
                Minimum = sub.MinDb,
                Maximum = sub.MaxDb,
                Value = Math.Clamp(sub.LeftLevelDb, sub.MinDb, sub.MaxDb),
                TickFrequency = sub.StepDb > 0 ? sub.StepDb : 1.0,
                IsEnabled = sub.SupportsVolume,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                ToolTip = $"Direct zero-latency hardware passthrough gain for {sub.Name} ({sub.MinDb:0.0} to {sub.MaxDb:+0.0;-0.0;0.0} dB)"
            };
            Grid.SetColumn(sld, 1);

            var valTxt = new TextBlock
            {
                Text = $"{sub.LeftLevelDb:+0.0;-0.0;0.0} dB",
                Foreground = (Brush)FindResource("Cyan"),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(valTxt, 2);

            var chkMute = new CheckBox
            {
                Content = "Mute",
                IsChecked = sub.IsMuted,
                IsEnabled = sub.SupportsMute,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
                ToolTip = $"Mute or unmute zero-latency analog passthrough monitoring for {sub.Name}"
            };
            Grid.SetColumn(chkMute, 3);

            sld.ValueChanged += (_, _) =>
            {
                float db = (float)Math.Round(sld.Value, 1);
                valTxt.Text = $"{db:+0.0;-0.0;0.0} dB";
                if (!sub.IsBackedByRegistry)
                {
                    _studio.CoreAudio.TrySetTopologySubunit(endpointId, sub.LocalId, db, db, chkMute.IsChecked == true);
                }
                SetStatus($"{sub.Name} level set to {db:+0.0;-0.0;0.0} dB.");
            };

            chkMute.Checked += (_, _) =>
            {
                float db = (float)sld.Value;
                if (!sub.IsBackedByRegistry)
                {
                    _studio.CoreAudio.TrySetTopologySubunit(endpointId, sub.LocalId, db, db, true);
                }
                SetStatus($"{sub.Name} Muted.");
            };

            chkMute.Unchecked += (_, _) =>
            {
                float db = (float)sld.Value;
                if (!sub.IsBackedByRegistry)
                {
                    _studio.CoreAudio.TrySetTopologySubunit(endpointId, sub.LocalId, db, db, false);
                }
                SetStatus($"{sub.Name} Unmuted (Direct hardware monitoring active).");
            };

            grid.Children.Add(titleStack);
            grid.Children.Add(sld);
            grid.Children.Add(valTxt);
            grid.Children.Add(chkMute);

            card.Child = grid;
            PanelAnalogSubunits.Children.Add(card);
        }
    }

    // =========================================================================
    // TAB 5: MICROPHONE, LINE-IN & RECORDING DSP
    // =========================================================================

    private void CmbCaptureEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        if (CmbCaptureEndpoints.SelectedItem is RealtekEndpointInfo ep)
        {
            _activeCaptureEndpoint = ep;
            LoadSelectedCaptureEndpoint();
            SetStatus($"Selected Recording Endpoint: {ep.FriendlyName} ({ep.StateText}).");
        }
    }

    private void BtnSetDefaultCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCaptureEndpoint == null)
            return;

        if (_studio.PolicyConfig.TrySetDefaultEndpoint(_activeCaptureEndpoint.EndpointId))
        {
            SetStatus($"Set '{_activeCaptureEndpoint.FriendlyName}' as Default Windows Recording Device.");
            RefreshAllData();
        }
        else
        {
            SetStatus($"Could not set '{_activeCaptureEndpoint.FriendlyName}' as default recording device.");
        }
    }

    private void BtnEnableStereoMix_Click(object sender, RoutedEventArgs e)
    {
        var stereoMix = _captureEndpoints.FirstOrDefault(c => c.FriendlyName.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase));
        if (stereoMix == null)
        {
            SetStatus("Stereo Mix endpoint was not found on this codec.");
            return;
        }

        bool ok = _studio.PolicyConfig.TrySetEndpointVisibility(stereoMix.EndpointId, true);
        SetStatus(ok
            ? $"Enabled Stereo Mix ({stereoMix.EndpointId}) visibility in Windows Audio."
            : "Requested Stereo Mix visibility via PolicyConfig.");
        RefreshAllData();
    }

    private void SldCaptureVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _activeCaptureEndpoint == null)
            return;

        float vol = (float)SldCaptureVol.Value;
        bool muted = ChkCaptureMute.IsChecked == true;
        TxtCaptureVol.Text = $"{vol:0}%";
        _studio.CoreAudio.TrySetEndpointMasterVolume(_activeCaptureEndpoint.EndpointId, vol, muted);
    }

    private void ChkCaptureMute_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activeCaptureEndpoint == null)
            return;

        float vol = (float)SldCaptureVol.Value;
        bool muted = ChkCaptureMute.IsChecked == true;
        _studio.CoreAudio.TrySetEndpointMasterVolume(_activeCaptureEndpoint.EndpointId, vol, muted);
        SetStatus($"Recording Input '{_activeCaptureEndpoint.FriendlyName}' {(muted ? "Muted" : "Unmuted")}.");
    }

    private void SldMicBoostDb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float db = (float)Math.Round(SldMicBoostDb.Value, 1);
        TxtMicBoostDb.Text = $"+{db:0.0} dB";

        if (_suppressUiEvents || _activeCaptureEndpoint == null)
            return;

        var st = BuildCaptureStateFromUi();
        _studio.ApplyCaptureState(st);
        SetStatus($"Analog Microphone Pre-Amp Boost set to +{db:0.0} dB.");
    }

    private void CaptureEffectToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activeCaptureEndpoint == null)
            return;

        var st = BuildCaptureStateFromUi();
        _studio.ApplyCaptureState(st);
        if (sender is CheckBox chk)
        {
            SetStatus($"Recording DSP: {chk.Content} => {(chk.IsChecked == true ? "ON" : "OFF")}");
        }
    }

    private RealtekCaptureState BuildCaptureStateFromUi()
    {
        return new RealtekCaptureState
        {
            EndpointId = _activeCaptureEndpoint?.EndpointId ?? string.Empty,
            MasterVolumePercent = (float)SldCaptureVol.Value,
            MasterMuted = ChkCaptureMute.IsChecked == true,
            MicBoostDb = (float)SldMicBoostDb.Value,
            NoiseSuppressionEnabled = ChkMicNoiseSuppression.IsChecked == true,
            AcousticEchoCancellationEnabled = ChkMicAec.IsChecked == true,
            BeamFormingEnabled = ChkMicBeamForming.IsChecked == true,
            DcOffsetCancellationEnabled = ChkMicDcOffset.IsChecked == true,
            MonoMicrophoneEnabled = ChkMicMono.IsChecked == true,
            ConferenceModeEnabled = ChkMicConference.IsChecked == true,
            KeystrokeSuppressionEnabled = ChkMicKeystroke.IsChecked == true,
            DisableAllEnhancements = ChkCaptureDisableAll.IsChecked == true
        };
    }

    // =========================================================================
    // TAB 6: PHYSICAL JACK TOPOLOGY, LIVE JACK-SENSE, RETASKING & FX INSPECTOR
    // =========================================================================

    private static string ComputeEndpointFingerprint(List<RealtekEndpointInfo> endpoints)
    {
        return string.Join("|", endpoints
            .Where(e => e.IsRealtek || e.IsActive)
            .OrderBy(e => e.EndpointId, StringComparer.OrdinalIgnoreCase)
            .Select(e => $"{e.EndpointId}:{e.DeviceState}:{(e.IsDefaultMultimedia ? 1 : 0)}"));
    }

    private async void JackSenseTimer_Tick(object? sender, EventArgs e)
    {
        if (_isJackSensePolling || _isRetaskInProgress || WindowState == WindowState.Minimized || !IsVisible)
            return;

        _isJackSensePolling = true;
        try
        {
            var (allEndpoints, livePins) = await Task.Run(() =>
            {
                var eps = _studio.DiscoverEndpoints(includeInactiveRealtekPins: true);
                var pins = _studio.QueryLiveJackPins(eps);
                return (eps, pins);
            });

            if (_isRetaskInProgress)
                return;

            var physicalPins = livePins.Where(p => p.IsPhysicalJack).ToList();
            var plugChanges = new List<string>();
            bool needsFullRowRebuild = _jackRowsByWidget.Count == 0;

            foreach (var pin in physicalPins)
            {
                if (!_jackRowsByWidget.TryGetValue(pin.PinWidgetId, out var row))
                {
                    needsFullRowRebuild = true;
                    continue;
                }

                if (row.Pin.IsPluggedIn != pin.IsPluggedIn)
                {
                    row.PlugBadge.Text = pin.IsPluggedIn ? "● PLUGGED" : "○ IDLE";
                    row.PlugBadge.Foreground = pin.IsPluggedIn ? (Brush)FindResource("Green") : (Brush)FindResource("Muted");
                    string shortLoc = pin.PanelLocation.Replace(" Panel", "");
                    plugChanges.Add($"{shortLoc} 0x{pin.PinWidgetId:X2} ({pin.JackColorName}) -> {(pin.IsPluggedIn ? "PLUGGED" : "UNPLUGGED")}");
                }

                if (row.Pin.RetaskRoleCode != pin.RetaskRoleCode && !row.RoleCombo.IsDropDownOpen)
                {
                    SelectRoleComboByRetaskCode(row.RoleCombo, pin.RetaskRoleCode);
                }

                row.Pin = pin;
            }

            if (needsFullRowRebuild && physicalPins.Count > 0)
            {
                BuildJackPinsPanel(allEndpoints, livePins);
            }

            UpdateJackSensorBadge(physicalPins);

            string newFingerprint = ComputeEndpointFingerprint(allEndpoints);
            if (!string.Equals(newFingerprint, _lastEndpointFingerprint, StringComparison.Ordinal))
            {
                _lastEndpointFingerprint = newFingerprint;
                RefreshEndpointDropdownsOnly(allEndpoints);
            }

            if (plugChanges.Count > 0)
            {
                SetStatus($"Live Jack Sense: {string.Join(" • ", plugChanges)}");
            }
        }
        catch
        {
            // Keep polling resilient across transient audio device transitions
        }
        finally
        {
            _isJackSensePolling = false;
        }
    }

    private void RefreshEndpointDropdownsOnly(List<RealtekEndpointInfo> allEndpoints)
    {
        bool prevSuppress = _suppressUiEvents;
        bool playbackBecameActive = false;
        _suppressUiEvents = true;
        try
        {
            string? prevPbId = _activePlaybackEndpoint?.EndpointId;
            bool wasPbActive = _activePlaybackEndpoint?.IsActive ?? false;
            string? prevCapId = _activeCaptureEndpoint?.EndpointId;

            _playbackEndpoints = allEndpoints.Where(e => e.DataFlow == AudioEndpointFlow.Render).ToList();
            _captureEndpoints = allEndpoints.Where(e => e.DataFlow == AudioEndpointFlow.Capture).ToList();

            CmbPlaybackEndpoints.ItemsSource = _playbackEndpoints;
            _activePlaybackEndpoint = _playbackEndpoints.FirstOrDefault(e => e.EndpointId == prevPbId)
                                   ?? _playbackEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive && e.IsDefaultMultimedia)
                                   ?? _playbackEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive)
                                   ?? _playbackEndpoints.FirstOrDefault();
            CmbPlaybackEndpoints.SelectedItem = _activePlaybackEndpoint;

            if (!wasPbActive && _activePlaybackEndpoint?.IsActive == true)
                playbackBecameActive = true;

            CmbCaptureEndpoints.ItemsSource = _captureEndpoints;
            _activeCaptureEndpoint = _captureEndpoints.FirstOrDefault(e => e.EndpointId == prevCapId)
                                  ?? _captureEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive && e.IsDefaultMultimedia)
                                  ?? _captureEndpoints.FirstOrDefault(e => e.IsRealtek && e.IsActive)
                                  ?? _captureEndpoints.FirstOrDefault();
            CmbCaptureEndpoints.SelectedItem = _activeCaptureEndpoint;
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
        }

        if (playbackBecameActive && _activePlaybackEndpoint != null)
        {
            LoadSelectedPlaybackEndpoint();
        }
    }

    private void UpdateJackSensorBadge(List<JackPinInfo> physicalPins)
    {
        string source = physicalPins.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.LiveSensorSource))?.LiveSensorSource ?? string.Empty;
        if (source.Contains("Kernel KS", StringComparison.OrdinalIgnoreCase))
        {
            TxtJackSenseBadge.Text = "● LIVE KERNEL SENSE";
            TxtJackSenseBadge.Foreground = (Brush)FindResource("Green");
        }
        else if (source.Contains("RtCOM64", StringComparison.OrdinalIgnoreCase))
        {
            TxtJackSenseBadge.Text = "● LIVE RTCOM SENSE";
            TxtJackSenseBadge.Foreground = (Brush)FindResource("Green");
        }
        else
        {
            TxtJackSenseBadge.Text = "● AUTO ENDPOINT SENSE";
            TxtJackSenseBadge.Foreground = (Brush)FindResource("Cyan");
        }
    }

    private static void SelectRoleComboByRetaskCode(ComboBox roleCmb, byte retaskRoleCode)
    {
        for (int i = 0; i < roleCmb.Items.Count; i++)
        {
            if (roleCmb.Items[i] is ComboBoxItem cbi && cbi.Tag is byte code && code == retaskRoleCode)
            {
                roleCmb.SelectedIndex = i;
                return;
            }
        }
        if (roleCmb.Items.Count > 0)
            roleCmb.SelectedIndex = 0;
    }

    private void BuildJackPinsPanel(List<RealtekEndpointInfo>? discoveredEndpoints = null, List<JackPinInfo>? preloadedPins = null)
    {
        PanelJackPins.Children.Clear();
        _jackRowsByWidget.Clear();

        var pins = preloadedPins ?? _studio.QueryLiveJackPins(discoveredEndpoints);
        var physicalPins = pins.Where(p => p.IsPhysicalJack).ToList();
        UpdateJackSensorBadge(physicalPins);

        foreach (var pin in physicalPins)
        {
            var rowBorder = new Border
            {
                Background = (Brush)FindResource("PanelAlt"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Brush jackBrush;
            try
            {
                jackBrush = (Brush)new BrushConverter().ConvertFromString(pin.JackColorHex)!;
            }
            catch
            {
                jackBrush = (Brush)FindResource("Cyan");
            }

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = jackBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(dot, 0);

            var locTxt = new TextBlock
            {
                Text = $"{pin.PanelLocation.Replace(" Panel", "")} 0x{pin.PinWidgetId:X2}",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(locTxt, 1);

            var colorTxt = new TextBlock
            {
                Text = pin.JackColorName,
                ToolTip = $"{pin.JackColorName} • Widget 0x{pin.PinWidgetId:X2} ({pin.ConnectorType})",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(colorTxt, 2);

            var plugBadge = new TextBlock
            {
                Text = pin.IsPluggedIn ? "● PLUGGED" : "○ IDLE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = pin.IsPluggedIn ? (Brush)FindResource("Green") : (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Live physical jack connection state detected automatically by the Realtek codec sensor"
            };
            Grid.SetColumn(plugBadge, 3);

            var supportedRoles = DriverHardwareService.GetSupportedRolesForPin(pin);
            var roleCmb = new ComboBox
            {
                FontSize = 11.5,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(4, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Hardware-supported roles for Pin 0x{pin.PinWidgetId:X2} (Mask 0x{pin.SupportedRolesMask:X4})"
            };
            foreach (var choice in supportedRoles)
            {
                roleCmb.Items.Add(new ComboBoxItem
                {
                    Content = choice.Label,
                    Tag = choice.RetaskCode
                });
            }
            SelectRoleComboByRetaskCode(roleCmb, pin.RetaskRoleCode);
            Grid.SetColumn(roleCmb, 4);

            var btnRetask = new Button
            {
                Content = "Retask",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Margin = new Thickness(0),
                ToolTip = $"Immediately reassign Pin 0x{pin.PinWidgetId:X2} to the selected audio role inside the Realtek kernel driver"
            };

            var rowState = new JackRowUiState
            {
                Pin = pin,
                PlugBadge = plugBadge,
                RoleCombo = roleCmb,
                RetaskButton = btnRetask
            };
            _jackRowsByWidget[pin.PinWidgetId] = rowState;

            btnRetask.Click += async (_, _) =>
            {
                if (_isRetaskInProgress)
                    return;

                if (roleCmb.SelectedItem is ComboBoxItem cbi && cbi.Tag is byte newRoleCode)
                {
                    var currentPin = rowState.Pin;
                    var roleOpt = DriverHardwareService.GetRetaskRoleOption(newRoleCode);
                    _isRetaskInProgress = true;
                    btnRetask.IsEnabled = false;
                    BtnResetDefaultJacks.IsEnabled = false;
                    SetStatus($"Retasking Pin 0x{currentPin.PinWidgetId:X2} ({currentPin.JackColorName}) -> {roleOpt.Label}...");

                    try
                    {
                        var (ok, msg) = await Task.Run(() =>
                        {
                            bool success = _studio.DriverHardware.TryRetaskJackPin(currentPin, newRoleCode, _studio.RealtekCom, out string resultMsg);
                            return (success, resultMsg);
                        });

                        if (ok)
                        {
                            await Task.Delay(350);
                            RefreshAllData();
                        }
                        SetStatus(msg);
                    }
                    finally
                    {
                        btnRetask.IsEnabled = true;
                        BtnResetDefaultJacks.IsEnabled = true;
                        _isRetaskInProgress = false;
                    }
                }
            };
            Grid.SetColumn(btnRetask, 5);

            grid.Children.Add(dot);
            grid.Children.Add(locTxt);
            grid.Children.Add(colorTxt);
            grid.Children.Add(plugBadge);
            grid.Children.Add(roleCmb);
            grid.Children.Add(btnRetask);

            rowBorder.Child = grid;
            PanelJackPins.Children.Add(rowBorder);
        }
    }

    private async void BtnResetDefaultJacks_Click(object sender, RoutedEventArgs e)
    {
        if (_isRetaskInProgress)
            return;

        _isRetaskInProgress = true;
        BtnResetDefaultJacks.IsEnabled = false;
        SetStatus("Restoring all physical analog jacks to motherboard factory defaults...");
        try
        {
            var currentPins = _jackRowsByWidget.Values.Select(r => r.Pin).ToList();
            if (currentPins.Count == 0)
            {
                currentPins = _studio.QueryLiveJackPins();
            }

            var (ok, msg) = await Task.Run(() =>
            {
                bool success = _studio.DriverHardware.TryResetAllJacksToDefaults(currentPins, _studio.RealtekCom, out string resultMsg);
                return (success, resultMsg);
            });

            if (ok)
            {
                await Task.Delay(400);
                RefreshAllData();
            }
            SetStatus(msg);
        }
        finally
        {
            BtnResetDefaultJacks.IsEnabled = true;
            _isRetaskInProgress = false;
        }
    }

    private async void BtnApplyDriverTweaks_Click(object sender, RoutedEventArgs e)
    {
        _currentDriverTweaks.MuteRearWhenFrontHpPlugged = ChkDrvMuteRearOnHp.IsChecked == true;
        _currentDriverTweaks.IndependentFrontRearStreams = ChkDrvIndependentStreams.IsChecked == true;
        _currentDriverTweaks.FrontPanelHdAudioMode = ChkDrvFrontHdaMode.IsChecked == true;
        _currentDriverTweaks.AntiPopDisableIdlePowerDown = ChkDrvAntiPopPower.IsChecked == true;
        _currentDriverTweaks.SwapCenterLfeInDriver = ChkDrvSwapCenLfe.IsChecked == true;

        BtnApplyDriverTweaks.IsEnabled = false;
        SetStatus("Applying kernel driver tweaks...");
        try
        {
            var tweaksCopy = _currentDriverTweaks;
            var (_, msg) = await Task.Run(() =>
            {
                // Also notify RtCOM64 directly where supported
                _studio.RealtekCom.TrySetFrontPanelJackDetectionDisabled(!tweaksCopy.FrontPanelHdAudioMode);
                _studio.RealtekCom.TrySetMultiStreaming(tweaksCopy.IndependentFrontRearStreams, false);

                bool ok = _studio.DriverHardware.TryApplyDriverTweaks(tweaksCopy, out string statusMsg);
                return (ok, statusMsg);
            });
            SetStatus(msg);
        }
        finally
        {
            BtnApplyDriverTweaks.IsEnabled = true;
        }
    }

    private void TxtPropFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyPropertyFilter();
    }

    private void ApplyPropertyFilter()
    {
        string filter = TxtPropFilter.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            GridPropertiesInspector.ItemsSource = _allPropertyEntries;
            return;
        }

        GridPropertiesInspector.ItemsSource = _allPropertyEntries
            .Where(p =>
                p.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.KeyString.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.DecodedValue.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void BtnSendHdaVerb_Click(object sender, RoutedEventArgs e)
    {
        uint widget = ParseHexOrDec(TxtHdaWidget.Text, 0);
        uint verb = ParseHexOrDec(TxtHdaVerb.Text, 0xF00);
        uint payload = ParseHexOrDec(TxtHdaPayload.Text, 0);

        _studio.RealtekCom.TrySendHdaVerb(widget, verb, payload, out _, out _, out string statusMsg);
        SetStatus($"HDA Verb (Node=0x{widget:X2}, Verb=0x{verb:X3}, Payload=0x{payload:X2}): {statusMsg}");
    }

    private void BtnExportDiagReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Open-Source Community Diagnostic Report",
            Filter = "JSON Diagnostic Report (*.json)|*.json",
            FileName = $"SpectraRealtekDiag_{DateTime.Now:yyyyMMdd_HHmm}.json"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var report = new
            {
                GeneratedUtc = DateTime.UtcNow,
                HardwareTweaks = _studio.DriverHardware.ReadHardwareTweaks(),
                JackPins = _studio.QueryLiveJackPins(),
                Endpoints = _studio.DiscoverEndpoints(true),
                ActivePlaybackState = _activePlaybackEndpoint != null ? _studio.ReadPlaybackState(_activePlaybackEndpoint.EndpointId) : null,
                ActiveCaptureState = _activeCaptureEndpoint != null ? _studio.ReadCaptureState(_activeCaptureEndpoint.EndpointId) : null,
                ActivePlaybackProperties = _allPropertyEntries
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            SetStatus($"Exported full community diagnostic report to {dlg.FileName}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Diagnostic report export failed: {ex.Message}");
        }
    }

    // =========================================================================
    // GLOBAL IN-GAME HOTKEYS (Ctrl+Alt+E / N / L / K)
    // =========================================================================

    private void ChkGlobalHotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        if (ChkGlobalHotkeys.IsChecked == true)
        {
            RegisterGlobalHotkeys();
        }
        else
        {
            UnregisterGlobalHotkeys();
            SetStatus("Global In-Game Hotkeys disabled.");
        }
    }

    private void RegisterGlobalHotkeys()
    {
        if (_hwndSource == null || _hotkeysRegistered)
            return;

        IntPtr hwnd = _hwndSource.Handle;
        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;

        bool ok1 = RegisterHotKey(hwnd, HOTKEY_ID_TOGGLE_EQ, mods, 0x45);       // 'E'
        bool ok2 = RegisterHotKey(hwnd, HOTKEY_ID_NEXT_PRESET, mods, 0x4E);     // 'N'
        bool ok3 = RegisterHotKey(hwnd, HOTKEY_ID_TOGGLE_LOUDNESS, mods, 0x4C); // 'L'
        bool ok4 = RegisterHotKey(hwnd, HOTKEY_ID_TOGGLE_KARAOKE, mods, 0x4B);  // 'K'

        _hotkeysRegistered = ok1 || ok2 || ok3 || ok4;
        if (_hotkeysRegistered)
        {
            TxtRightStatus.Text = "Global Hotkeys Active (Ctrl+Alt+E/N/L/K) • Non-Admin RPC Active";
        }
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_hwndSource == null || !_hotkeysRegistered)
            return;

        IntPtr hwnd = _hwndSource.Handle;
        UnregisterHotKey(hwnd, HOTKEY_ID_TOGGLE_EQ);
        UnregisterHotKey(hwnd, HOTKEY_ID_NEXT_PRESET);
        UnregisterHotKey(hwnd, HOTKEY_ID_TOGGLE_LOUDNESS);
        UnregisterHotKey(hwnd, HOTKEY_ID_TOGGLE_KARAOKE);
        _hotkeysRegistered = false;
        TxtRightStatus.Text = "Open-Source Community Edition • Non-Admin RPC Active";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HOTKEY_ID_TOGGLE_EQ:
                    ToggleEqualizerShortcut();
                    handled = true;
                    break;
                case HOTKEY_ID_NEXT_PRESET:
                    CycleNextEqPresetShortcut();
                    handled = true;
                    break;
                case HOTKEY_ID_TOGGLE_LOUDNESS:
                    ToggleLoudnessShortcut();
                    handled = true;
                    break;
                case HOTKEY_ID_TOGGLE_KARAOKE:
                    ToggleKaraokeShortcut();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ToggleEqualizerShortcut()
    {
        ChkEqEnable.IsChecked = !(ChkEqEnable.IsChecked == true);
    }

    private void CycleNextEqPresetShortcut()
    {
        var presets = _studio.Presets.GetAllEqPresets();
        if (presets.Count == 0)
            return;

        int nextIdx = (CmbEqPresets.SelectedIndex + 1) % presets.Count;
        CmbEqPresets.SelectedIndex = nextIdx;
    }

    private void ToggleLoudnessShortcut()
    {
        ChkLoudnessEq.IsChecked = !(ChkLoudnessEq.IsChecked == true);
    }

    private void ToggleKaraokeShortcut()
    {
        ChkVoiceCancellation.IsChecked = !(ChkVoiceCancellation.IsChecked == true);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void SetStatus(string text)
    {
        TxtStatusBar.Text = $"[{DateTime.Now:HH:mm:ss}] {text}";
    }

    private static void SelectComboItemByTag(ComboBox cmb, string tagValue, int fallbackIndex)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
        if (cmb.Items.Count > fallbackIndex)
            cmb.SelectedIndex = fallbackIndex;
    }

    private static uint GetSelectedTagUInt(ComboBox cmb, uint fallback)
    {
        if (cmb.SelectedItem is ComboBoxItem item && item.Tag != null &&
            uint.TryParse(item.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out uint val))
        {
            return val;
        }
        return fallback;
    }

    private static uint ParseHexOrDec(string input, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
            return fallback;

        string s = input.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                return hexVal;
        }
        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint decVal))
            return decVal;
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rawHex))
            return rawHex;

        return fallback;
    }
}
