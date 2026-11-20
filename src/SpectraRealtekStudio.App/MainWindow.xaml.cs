using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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

        BuildEqualizerBandSliders();
        BuildRoomCorrectionChannelRows();
        PopulateEnvironmentPresets();
        InitializeSystemTray();

        _meterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _meterTimer.Tick += MeterTimer_Tick;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;

        if (startMinimized)
        {
            // Ensure data and presets initialize immediately even when starting hidden in tray
            RefreshPresetComboBox();
            RefreshAllData();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshPresetComboBox();
        RefreshAllData();
        _meterTimer.Start();
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

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _meterTimer.Stop();
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
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(valText, 0);
            _eqBandValueLabels[i] = valText;

            var slider = new Slider
            {
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
                ToolTip = $"{RealtekPropertyKeys.EqBandLabels[i]} Band (-12.0 dB to +12.0 dB • Double-click to reset to 0 dB)"
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
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text"),
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
                VerticalAlignment = VerticalAlignment.Center
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
                VerticalAlignment = VerticalAlignment.Center
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
        }
        finally
        {
            _suppressUiEvents = prevSuppress;
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

            BuildJackPinsPanel();
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

            // Tab 1: Equalizer
            ChkEqEnable.IsChecked = _currentPlaybackState.EqualizerEnabled;
            for (int i = 0; i < 10; i++)
            {
                double db = _currentPlaybackState.EqualizerBandsDb[i];
                _eqBandSliders[i].Value = db;
                UpdateEqBandLabel(i, db);
            }
            UpdateMatchedPresetSelection(_currentPlaybackState.EqualizerBandsDb);
            RenderEqCurve();

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
    // LIVE PEAK METER TIMER
    // =========================================================================

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_activePlaybackEndpoint == null || WindowState == WindowState.Minimized)
            return;

        if (_studio.CoreAudio.TryGetPeakMeter(_activePlaybackEndpoint.EndpointId, out _, out float lPeak, out float rPeak))
        {
            PbPeakLeft.Value = Math.Clamp(lPeak * 100.0, 0.0, 100.0);
            PbPeakRight.Value = Math.Clamp(rPeak * 100.0, 0.0, 100.0);
        }
    }

    // =========================================================================
    // HEADER & ENDPOINT BAR EVENT HANDLERS
    // =========================================================================

    private void SldHeaderMasterVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        float vol = (float)SldHeaderMasterVol.Value;
        bool muted = ChkHeaderMute.IsChecked == true;
        TxtHeaderMasterVol.Text = $"{vol:0}%";
        _studio.CoreAudio.TrySetEndpointMasterVolume(_activePlaybackEndpoint.EndpointId, vol, muted);
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

    private void CmbPlaybackEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        if (CmbPlaybackEndpoints.SelectedItem is RealtekEndpointInfo ep)
        {
            _activePlaybackEndpoint = ep;
            LoadSelectedPlaybackEndpoint();
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

    private void OnEqBandSliderChanged(int bandIndex)
    {
        double db = Math.Round(_eqBandSliders[bandIndex].Value, 1);
        UpdateEqBandLabel(bandIndex, db);

        if (_suppressUiEvents || _activePlaybackEndpoint == null)
            return;

        double[] bands = GetCurrentSliderBands();
        bool eqEnabled = ChkEqEnable.IsChecked == true;
        if (!eqEnabled)
        {
            _suppressUiEvents = true;
            ChkEqEnable.IsChecked = true;
            _suppressUiEvents = false;
            eqEnabled = true;
        }

        _studio.ApplyEqualizer(_activePlaybackEndpoint.EndpointId, eqEnabled, bands);
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
        TxtActivePresetBadge.Text = $"Active Preset: {matched}";

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
        double[] bands = GetCurrentSliderBands();
        _studio.ApplyEqualizer(_activePlaybackEndpoint.EndpointId, enabled, bands);
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
            TxtActivePresetBadge.Text = $"Active Preset: {preset.Name}";
            TxtCustomPresetName.Text = preset.IsBuiltIn ? string.Empty : preset.Name;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        _studio.ApplyEqualizer(_activePlaybackEndpoint.EndpointId, true, preset.BandsDb);
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

        // Filled gradient polygon under curve
        var fillPoly = new Polygon
        {
            Fill = new LinearGradientBrush(
                eqOn ? Color.FromArgb(85, 34, 232, 255) : Color.FromArgb(40, 127, 139, 176),
                Color.FromArgb(5, 255, 43, 214),
                90.0)
        };
        var fillPts = new PointCollection { new Point(points[0].X, midY) };
        foreach (var pt in splinePoints)
            fillPts.Add(pt);
        fillPts.Add(new Point(points[^1].X, midY));
        fillPoly.Points = fillPts;
        CanvasEqCurve.Children.Add(fillPoly);

        // Main frequency response curve line
        var curveLine = new Polyline
        {
            Points = splinePoints,
            Stroke = eqOn ? (Brush)FindResource("Cyan") : (Brush)FindResource("Muted"),
            StrokeThickness = 2.4
        };
        CanvasEqCurve.Children.Add(curveLine);

        // 10 Band node circles
        for (int i = 0; i < 10; i++)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = bands[i] >= 0 ? (Brush)FindResource("Cyan") : (Brush)FindResource("Magenta"),
                Stroke = new SolidColorBrush(Color.FromRgb(11, 13, 20)),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(dot, points[i].X - 4);
            Canvas.SetTop(dot, points[i].Y - 4);
            CanvasEqCurve.Children.Add(dot);
        }
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
                Margin = new Thickness(10, 0, 10, 0)
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
                Margin = new Thickness(0)
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
    // TAB 6: PHYSICAL JACK TOPOLOGY, RETASKING, DRIVER TWEAKS & FX INSPECTOR
    // =========================================================================

    private static readonly (byte Code, string Label)[] RetaskRoleChoices =
    {
        (0x0, "Line Out / Speaker Out"),
        (0x2, "Headphone Out"),
        (0x8, "Line In"),
        (0xA, "Microphone In"),
        (0x4, "SPDIF Digital Out")
    };

    private void BuildJackPinsPanel()
    {
        PanelJackPins.Children.Clear();
        var comPlugMap = _studio.RealtekCom.TryQueryJackPlugStatesFromCom();
        var pins = _studio.DriverHardware.ReadJackPins(comPlugMap);

        foreach (var pin in pins.Where(p => p.IsPhysicalJack))
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
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
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(colorTxt, 2);

            var plugBadge = new TextBlock
            {
                Text = pin.IsPluggedIn ? "● PLUGGED" : "○ IDLE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = pin.IsPluggedIn ? (Brush)FindResource("Green") : (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(plugBadge, 3);

            var roleCmb = new ComboBox
            {
                FontSize = 11.5,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(4, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            int selectedIdx = 0;
            for (int r = 0; r < RetaskRoleChoices.Length; r++)
            {
                var choice = RetaskRoleChoices[r];
                roleCmb.Items.Add(new ComboBoxItem
                {
                    Content = choice.Label,
                    Tag = choice.Code
                });
                if (choice.Code == pin.DefaultDeviceCode)
                    selectedIdx = r;
            }
            roleCmb.SelectedIndex = selectedIdx;
            Grid.SetColumn(roleCmb, 4);

            var btnRetask = new Button
            {
                Content = "Retask",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Margin = new Thickness(0)
            };
            btnRetask.Click += (_, _) =>
            {
                if (roleCmb.SelectedItem is ComboBoxItem cbi && cbi.Tag is byte newCode)
                {
                    if (_studio.DriverHardware.TryRetaskJackPin(pin, newCode, out string msg))
                    {
                        SetStatus(msg);
                        BuildJackPinsPanel();
                    }
                    else
                    {
                        SetStatus(msg);
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

    private void BtnApplyDriverTweaks_Click(object sender, RoutedEventArgs e)
    {
        _currentDriverTweaks.MuteRearWhenFrontHpPlugged = ChkDrvMuteRearOnHp.IsChecked == true;
        _currentDriverTweaks.IndependentFrontRearStreams = ChkDrvIndependentStreams.IsChecked == true;
        _currentDriverTweaks.FrontPanelHdAudioMode = ChkDrvFrontHdaMode.IsChecked == true;
        _currentDriverTweaks.AntiPopDisableIdlePowerDown = ChkDrvAntiPopPower.IsChecked == true;
        _currentDriverTweaks.SwapCenterLfeInDriver = ChkDrvSwapCenLfe.IsChecked == true;

        // Also notify RtCOM64 directly where supported
        _studio.RealtekCom.TrySetFrontPanelJackDetectionDisabled(!_currentDriverTweaks.FrontPanelHdAudioMode);
        _studio.RealtekCom.TrySetMultiStreaming(_currentDriverTweaks.IndependentFrontRearStreams, false);

        if (_studio.DriverHardware.TryApplyDriverTweaks(_currentDriverTweaks, out string msg))
        {
            SetStatus(msg);
        }
        else
        {
            SetStatus(msg);
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
                JackPins = _studio.DriverHardware.ReadJackPins(),
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
