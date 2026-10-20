using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Sakir.Core;
using Sakir.Sensors;

namespace Sakir;

public partial class MainWindow : Window
{
    private readonly PanelService _service;
    private volatile bool _previewBusy;
    private bool _restoring;

    public MainWindow(PanelService service)
    {
        InitializeComponent();
        _service = service;

        // restore saved settings without echoing them straight back through the
        // change handlers
        _restoring = true;
        FpsSlider.Value = _service.Config.Fps;
        BrightSlider.Value = _service.Config.Brightness;
        ScaleSlider.Value = _service.TextScale;
        foreach (var rb in new[] { ModeAuto, ModeDash, ModeMusic, ModeGame, ModeMirror })
            rb.IsChecked = (string?)rb.Tag == _service.Config.Mode;
        if (ModeAuto.IsChecked != true && ModeDash.IsChecked != true &&
            ModeMusic.IsChecked != true && ModeGame.IsChecked != true && ModeMirror.IsChecked != true)
            ModeAuto.IsChecked = true;
        AutoStart.IsChecked = Autostart.IsEnabled();
        _restoring = false;

        LoadWindowList();
        SetupSupportLinks();

        _service.FrameRendered += OnFrame;
        Loaded += (_, _) => Refresh();
        SourceInitialized += (_, _) => UseDarkTitleBar();

        var ui = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        ui.Tick += (_, _) => Refresh();
        ui.Start();
    }

    /// <summary>
    /// The title bar is the window's non-client area - Windows draws it, not
    /// WPF, so it follows the OS theme and stays white next to our dark content
    /// whenever the user runs Windows in light mode. This asks DWM to draw it
    /// dark regardless.
    ///
    /// Attribute 20 is the documented one; builds before 20H1 used 19. Trying
    /// both is the usual approach and a wrong attribute is simply ignored.
    /// </summary>
    private void UseDarkTitleBar()
    {
        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Refresh()
    {
        DeviceText.Text = _service.DeviceInfo;

        // A stalled sensor thread no longer freezes the panel, but the readings
        // do go stale - say so instead of quietly showing old numbers.
        string sensors = _service.SensorsAlive == DateTime.MinValue
            ? "    sensors: starting"
            : (DateTime.Now - _service.SensorsAlive).TotalSeconds > 8
                ? "    sensors: STALLED"
                : "";

        StatsText.Text =
            $"scene: {_service.ActiveScene}    {_service.ActualFps:0.0} fps    GPU: {_service.GpuVendor}" +
            (_service.CpuTempAvailable ? "    CPU temp: on" : "    CPU temp: needs admin") +
            sensors +
            (_service.PanelStalled ? "    PANEL NOT RESPONDING" : "");

        FpsLabel.Text = $"FRAME RATE  -  {(int)FpsSlider.Value} fps  (panel maxes out near {PanelService.MaxFps})";
        BrightLabel.Text = $"BRIGHTNESS  -  {(int)BrightSlider.Value}%";
        ScaleLabel.Text = $"TEXT SIZE  -  {ScaleSlider.Value:0.00}x  (bigger text means less fits)";

        if (!SystemSensors.IsElevated)
        {
            ElevateButton.Visibility = Visibility.Visible;
            NoteText.Text = "CPU temperature needs administrator rights (LibreHardwareMonitor loads a "
                          + "kernel driver). Everything else already works. Enabling \"Start with Windows\" "
                          + "registers an elevated task, so it runs without a prompt at login.";
        }
        else
        {
            ElevateButton.Visibility = Visibility.Collapsed;
            NoteText.Text = _service.LastError ?? "";
        }
    }

    /// <summary>
    /// The render thread hands us a bitmap it no longer owns. Convert on the UI
    /// thread and always dispose, and drop frames while one is still in flight
    /// so a slow UI can never back up the panel loop.
    /// </summary>
    private void OnFrame(System.Drawing.Bitmap bmp)
    {
        if (_previewBusy) { bmp.Dispose(); return; }
        _previewBusy = true;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;   // detach from the stream
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();

                Preview.Source = img;
            }
            catch { }
            finally
            {
                bmp.Dispose();
                _previewBusy = false;
            }
        });
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_service is null || _restoring) return;
        // WinForms also defines RadioButton, so qualify the WPF one
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag }) _service.Mode = tag;
    }

    private void Fps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_service is null || _restoring) return;
        _service.Fps = (int)e.NewValue;
    }

    private void Bright_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_service is null || _restoring) return;
        _service.SetBrightness((int)e.NewValue);
    }

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_service is null || _restoring) return;
        _service.TextScale = e.NewValue;
    }

    // ---- mirror source picker ---------------------------------------------
    private sealed record SourceItem(string Label, string Process, string Title)
    {
        public override string ToString() => Label;
    }

    private void LoadWindowList()
    {
        _restoring = true;
        try
        {
            var items = new List<SourceItem> { new("Whole screen", "", "") };
            foreach (Capture.WindowInfo w in Capture.WindowCapture.List())
                items.Add(new SourceItem(w.Display, w.ProcessName, w.Title));

            MirrorSource.ItemsSource = items;

            SourceItem? current = items.FirstOrDefault(i =>
                i.Process.Equals(_service.MirrorProcess, StringComparison.OrdinalIgnoreCase) &&
                (i.Title == _service.MirrorTitle || i.Process.Length > 0));
            MirrorSource.SelectedItem = current ?? items[0];
        }
        finally { _restoring = false; }
    }

    // ---- support links -----------------------------------------------------
    private void SetupSupportLinks()
    {
        bool donate = !string.IsNullOrWhiteSpace(_service.Config.DonateUrl);
        bool project = !string.IsNullOrWhiteSpace(_service.Config.ProjectUrl);

        DonateButton.Visibility = donate ? Visibility.Visible : Visibility.Collapsed;
        ProjectButton.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        SupportPanel.Visibility = (donate || project) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Donate_Click(object sender, RoutedEventArgs e) => OpenLink(_service.Config.DonateUrl);
    private void Project_Click(object sender, RoutedEventArgs e) => OpenLink(_service.Config.ProjectUrl);

    private static void OpenLink(string url)
    {
        // only ever hand the shell an http(s) URL, and only from our own config
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }

    private void MirrorSource_DropDownOpened(object sender, EventArgs e) => LoadWindowList();

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => LoadWindowList();

    private void MirrorSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_service is null || _restoring) return;
        if (MirrorSource.SelectedItem is SourceItem s)
        {
            _service.SetMirrorTarget(s.Process, s.Title);
            ModeMirror.IsChecked = true;      // picking a source implies you want it
        }
    }

    private void OpenBackgrounds_Click(object sender, RoutedEventArgs e)
    {
        string dir = _service.Games.BackgroundsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(AutoStart.IsChecked == true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Autostart",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            AutoStart.IsChecked = Autostart.IsEnabled();
        }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch { /* user declined the UAC prompt */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // closing hides to tray; the tray menu exits for real
        e.Cancel = true;
        Hide();
    }

    public void ReallyClose()
    {
        _service.FrameRendered -= OnFrame;
    }
}
