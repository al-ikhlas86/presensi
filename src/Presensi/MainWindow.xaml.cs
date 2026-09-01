using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Presensi.Filters;
using Presensi.Logging;
using Presensi.Services;
// OpenCvSharp.Window (GUI window OpenCV) vs System.Windows.Window (WPF) -
// keduanya masuk lewat "using OpenCvSharp;" di atas, disambiguasi eksplisit.
using Window = System.Windows.Window;

namespace Presensi;

public partial class MainWindow : Window
{
    // Jarak minimal antar-panggilan identifikasi wajah - kamera capture
    // ~12x/detik (lihat CameraService), TAPI memanggil API identifikasi
    // SETIAP frame itu percuma & boros (server + jaringan sekolah kena
    // beban tanpa manfaat - wajah orang yang sama tidak berubah tiap 80ms).
    // Pola sama persis dgn web Absen yang sudah terbukti (poll 300-600ms).
    private static readonly TimeSpan IdentifyThrottle = TimeSpan.FromMilliseconds(700);

    // Setelah 1 presensi berhasil, JEDA dulu sebelum menerima presensi
    // berikutnya - cegah 1 orang yang masih berdiri di depan kamera
    // ke-scan berkali-kali beruntun jadi banyak baris presensi identik.
    private static readonly TimeSpan CooldownAfterSuccess = TimeSpan.FromSeconds(3);

    private readonly ICameraService _camera = new CameraService();
    private readonly IBarcodeScannerService _scanner = new SerialBarcodeScannerService();
    private IAttendanceApiClient _api;
    private readonly SoundService _sound = new();
    private AppConfig _config;

    private List<IPreviewFilter> _filters = new();
    private IPreviewFilter _activeFilter = new NoFilter();

    private readonly DispatcherTimer _modeTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    // Cek berkala apakah scanner yang sudah pernah dipasangkan (ScannerPort di
    // appsettings.json) masih/kembali tersambung - scanner USB kiosk kadang
    // lepas-colok sendiri seharian, bukan cuma soal nyambung sekali di awal.
    private readonly DispatcherTimer _scannerReconnectTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    // Riwayat presensi live di panel kanan (diminta user 2026-09-01) - dibatasi
    // 100 baris terbaru, kiosk ini jalan berhari-hari tanpa restart jadi
    // daftarnya tidak boleh menumpuk tak terbatas (pola sama dgn Log.cs).
    private readonly ObservableCollection<string> _riwayatPresensi = new();
    private const int MaxRiwayat = 100;

    private AttendanceMode _mode = AttendanceMode.Masuk;
    private DateTime _lastIdentifyAttempt = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _busyFlag; // 0/1, dijaga Interlocked - cegah tumpang-tindih panggilan API

    // Dipakai TambahRiwayat menahan duplikat beruntun (lihat catatan di sana).
    private string? _lastRiwayatKey;
    private DateTime _lastRiwayatAt = DateTime.MinValue;
    private static readonly TimeSpan RiwayatDedupWindow = TimeSpan.FromSeconds(30);

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _api = BuildApiClient(_config);
        ApplyDisplayMode();
        ApplyRiwayatPanelVisibility();

#if !NET48
        // Mulai siapkan model landmark wajah di BACKGROUND sedini mungkin
        // (bukan ditunda sampai user pertama kali pakai filter stiker) -
        // supaya kalau memang perlu diunduh (~64MB, sekali per-PC), sudah
        // selesai/lagi jalan duluan sebelum benar2 dibutuhkan. Gagal di sini
        // (mis. tidak ada internet) TIDAK mengganggu apa pun - lihat
        // LandmarkModelService, filter otomatis fallback ke mode kotak biasa.
        _ = Filters.LandmarkModelService.EnsureLoadedAsync();
#endif

        FilterManager.EnsureSeeded();
        ReloadFilters();

        UpdateModeDisplay();
        _modeTimer.Tick += (_, _) => UpdateModeDisplay();
        _modeTimer.Start();

        RiwayatList.ItemsSource = _riwayatPresensi;

        _camera.PreviewFrameCaptured += OnPreviewFrameCaptured;
        _camera.RawFrameCaptured += OnRawFrameCaptured;
        _camera.CameraError += (_, msg) => Dispatcher.Invoke(() => StatusText.Text = "Kamera bermasalah: " + msg);
        _scanner.BarcodeScanned += OnBarcodeScanned;
        _scanner.ScannerError += (_, msg) => Dispatcher.Invoke(() =>
        {
            StatusText.Text = "Scanner bermasalah: " + msg;
            UpdateScannerStatus();
        });

        // Otomatis sambung lagi ke port scanner terakhir yang berhasil dipasangkan -
        // supaya PC yang dimatikan/software ditutup lalu dibuka lagi TIDAK perlu
        // klik "Sambungkan Scanner" manual tiap kali (diminta user 2026-09-01).
        var savedPort = _config.ScannerPort;
        if (!string.IsNullOrWhiteSpace(savedPort))
        {
            // "!" krn net48 (reference assemblies dari NuGet, lihat catatan
            // multi-target di Presensi.csproj) belum mengenal anotasi
            // [NotNullWhen] di IsNullOrWhiteSpace spt net8.0-windows, jadi
            // compiler tidak bisa mempersempit null di sana - bukan bug.
            _scanner.Connect(savedPort!);
        }
        UpdateScannerStatus();
        _scannerReconnectTimer.Tick += (_, _) => TryReconnectScannerIfNeeded();
        _scannerReconnectTimer.Start();

        Loaded += (_, _) => _camera.Start();
    }

    private void ApplyDisplayMode()
    {
        switch (_config.DisplayMode)
        {
            case "default":
                // Ukuran baku 1024x720 (sama dgn nilai awal di MainWindow.xaml
                // sebelum fitur mode tampilan ini ada) - tetap bisa diubah
                // manual sesudahnya, beda dari "bebas" yang TIDAK menyentuh
                // ukuran sama sekali (mempertahankan posisi/ukuran terakhir).
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                Width = 1024;
                Height = 720;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                break;
            case "fullscreen":
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                break;
            case "4:3":
                SetWindowedAspect(4.0 / 3.0);
                break;
            case "16:9":
                SetWindowedAspect(16.0 / 9.0);
                break;
            case "1:1":
                SetWindowedAspect(1.0);
                break;
            default: // "bebas" - ukuran/posisi TIDAK disentuh, bisa diubah manual
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                break;
        }
    }

    // Panel "Presensi Terbaru" opsional (diminta user 2026-09-01) - kolom
    // spacer & panelnya sama2 dikecilkan ke 0 saat dimatikan, BUKAN cuma
    // Visibility=Collapsed di Border-nya saja, supaya area preview kamera
    // ikut melebar mengisi ruang yang dibebaskan, bukan menyisakan jarak
    // kosong di kanan.
    private void ApplyRiwayatPanelVisibility()
    {
        if (_config.ShowRiwayatPanel)
        {
            RiwayatSpacerColumn.Width = new GridLength(16);
            RiwayatPanelColumn.Width = new GridLength(300);
            RiwayatPanelBorder.Visibility = Visibility.Visible;
        }
        else
        {
            RiwayatSpacerColumn.Width = new GridLength(0);
            RiwayatPanelColumn.Width = new GridLength(0);
            RiwayatPanelBorder.Visibility = Visibility.Collapsed;
        }
    }

    // Tinggi diambil dari area kerja layar (BUKAN angka hardcode) supaya
    // proporsional di monitor kiosk mana pun, lebar dihitung dari rasio.
    private void SetWindowedAspect(double ratio)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        double h = SystemParameters.WorkArea.Height * 0.85;
        Height = h;
        Width = h * ratio;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void TryReconnectScannerIfNeeded()
    {
        if (_scanner.IsConnected) return;
        var savedPort = _config.ScannerPort;
        if (string.IsNullOrWhiteSpace(savedPort)) return;
        _scanner.Connect(savedPort!); // lihat catatan "!" di konstruktor
        UpdateScannerStatus();
    }

    private void UpdateScannerStatus()
    {
        ScannerStatusText.Text = _scanner.IsConnected
            ? $"Scanner: tersambung ({_config.ScannerPort})"
            : "Scanner: belum tersambung";
    }

    private void TambahRiwayat(string? nama, AttendanceMode mode)
    {
        if (string.IsNullOrWhiteSpace(nama)) return;

        // _api Stub SELALU "berhasil" dgn nama palsu yang SAMA persis tiap ~3
        // detik selama ada wajah di depan kamera (appsettings.json belum diisi
        // KioskToken sungguhan) - itu murni artefak mode uji, bukan presensi
        // sungguhan, jadi TIDAK dicatat ke panel (dilaporkan user "berisik",
        // panel kena spam terus 2026-09-01).
        if (_api is StubAttendanceApiClient) return;

        var jenis = mode == AttendanceMode.Masuk ? "masuk" : "pulang";
        var key = nama + "|" + jenis;
        // Jaga tambahan: tahan duplikat ORANG+JENIS yang SAMA kalau berulang
        // dalam waktu singkat (mis. wajah masih di depan kamera pas cooldown
        // habis) - presensi sungguhan seharusnya ditolak server ("sudah
        // presensi hari ini") sebelum sampai sini, ini cuma jaring pengaman.
        if (key == _lastRiwayatKey && DateTime.UtcNow - _lastRiwayatAt < RiwayatDedupWindow) return;
        _lastRiwayatKey = key;
        _lastRiwayatAt = DateTime.UtcNow;

        _riwayatPresensi.Insert(0, $"{nama} telah melakukan presensi {jenis} - {DateTime.Now:HH:mm:ss}");
        while (_riwayatPresensi.Count > MaxRiwayat) _riwayatPresensi.RemoveAt(_riwayatPresensi.Count - 1);
    }

    private static IAttendanceApiClient BuildApiClient(AppConfig config)
    {
        // Token kiosk KOSONG = belum dikonfigurasi (appsettings.json baru
        // dibuat pertama kali) - pakai Stub sementara drpd nge-spam error
        // "tidak bisa menghubungi server" ke Absen. Isi appsettings.json
        // (di sebelah .exe) lalu buka ulang aplikasi begitu token sudah ada.
        if (string.IsNullOrWhiteSpace(config.KioskToken))
        {
            Logging.Log.Warn("KioskToken kosong di appsettings.json - jalan mode UJI (tidak benar-benar mengirim presensi).");
            return new StubAttendanceApiClient();
        }
        return new AbsenAttendanceApiClient { BaseUrl = config.BaseUrl, KioskToken = config.KioskToken };
    }

    private void UpdateModeDisplay()
    {
        _mode = ScheduleService.GetCurrentMode(_config);
        ModeText.Text = "Mode: " + (_mode == AttendanceMode.Masuk ? "MASUK" : "PULANG");
        ModeText.Foreground = _mode == AttendanceMode.Masuk
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
            : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        ModeScheduleText.Text = $"(otomatis - masuk sblm {_config.JamPulang}, pulang mulai {_config.JamPulang})";
    }

    private void ReloadFilters()
    {
        var previousName = _activeFilter.DisplayName;
        foreach (var f in _filters)
        {
            if (f is IDisposable d) d.Dispose();
        }

        _filters = FilterManager.LoadAll();
        FilterCombo.ItemsSource = _filters;

        var match = _filters.FirstOrDefault(f => f.DisplayName == previousName) ?? _filters[0];
        FilterCombo.SelectedItem = match;
        _activeFilter = match;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            UpdateModeDisplay();
            ApplyDisplayMode();
            ApplyRiwayatPanelVisibility();
        }
    }

    private void ManageFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterManagerWindow { Owner = this };
        dialog.ShowDialog();
        if (dialog.FiltersChanged)
        {
            ReloadFilters();
        }
    }

    private void FilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is IPreviewFilter filter) _activeFilter = filter;
    }

    private void TogglePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _camera.PreviewEnabled = !_camera.PreviewEnabled;
        TogglePreviewButton.Content = _camera.PreviewEnabled ? "Matikan Preview" : "Nyalakan Preview";
        PreviewOffText.Visibility = _camera.PreviewEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (!_camera.PreviewEnabled) PreviewImage.Source = null; // lepas bitmap lama, jangan biarkan gambar beku nyangkut
    }

    private void ScannerButton_Click(object sender, RoutedEventArgs e)
    {
        // Dialog pemilihan port sengaja SEDERHANA (v1) - daftar port lalu
        // pilih yang pertama tersedia. Diganti dialog pilihan resmi kalau
        // di lapangan ternyata >1 device serial terpasang bersamaan.
        var ports = SerialBarcodeScannerService.GetAvailablePorts();
        if (ports.Length == 0)
        {
            MessageBox.Show(this, "Tidak ada port serial terdeteksi. Pastikan scanner sudah tersambung.", "Scanner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _scanner.Connect(ports[0]);
        if (_scanner.IsConnected)
        {
            // Disimpan supaya kali berikutnya PC dinyalakan/app dibuka lagi,
            // scanner ini otomatis tersambung sendiri (lihat konstruktor).
            _config.ScannerPort = ports[0];
            AppConfig.Save(_config);
        }
        UpdateScannerStatus();
        MessageBox.Show(this, _scanner.IsConnected ? $"Tersambung ke {ports[0]}." : "Gagal tersambung.", "Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------
    // PENTING: frame di sini adalah frame PREVIEW (sudah di-mirror), BUKAN
    // frame mentah yang dikirim ke pengenalan wajah - lihat OnRawFrameCaptured
    // di bawah utk itu. Filter dekoratif HANYA boleh disentuhkan di sini.
    // ------------------------------------------------------------------
    private void OnPreviewFrameCaptured(object? sender, Mat frame)
    {
        try
        {
            _activeFilter.Apply(frame);
            var bitmap = frame.ToBitmapSource();
            bitmap.Freeze(); // wajib - dibuat di background thread, dipakai di UI thread
            Dispatcher.BeginInvoke(() => PreviewImage.Source = bitmap);
        }
        catch (Exception ex)
        {
            Log.Error("Gagal render preview", ex);
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Frame MENTAH tanpa filter apa pun - satu-satunya yang boleh dikirim
    // ke server pengenalan wajah. Dipanggil dari background thread milik
    // CameraService, BUKAN UI thread - jangan sentuh elemen UI langsung
    // di sini (lihat Dispatcher.Invoke di HandleAttendanceResult).
    // ------------------------------------------------------------------
    private void OnRawFrameCaptured(object? sender, Mat frame)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now < _cooldownUntil) return;
            if (now - _lastIdentifyAttempt < IdentifyThrottle) return;
            if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0) return; // sedang ada panggilan lain berjalan

            _lastIdentifyAttempt = now;
            Cv2.ImEncode(".jpg", frame, out byte[] jpegBytes);
            _ = IdentifyAsync(jpegBytes); // fire-and-forget disengaja - loop capture tidak boleh menunggu HTTP
        }
        finally
        {
            frame.Dispose();
        }
    }

    private async Task IdentifyAsync(byte[] jpegBytes)
    {
        try
        {
            var result = await _api.IdentifyFaceAsync(jpegBytes, _mode).ConfigureAwait(false);
            HandleAttendanceResult(result);
        }
        catch (Exception ex)
        {
            Log.Error("Gagal memanggil layanan identifikasi wajah", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busyFlag, 0);
        }
    }

    private async void OnBarcodeScanned(object? sender, string barcode)
    {
        if (DateTime.UtcNow < _cooldownUntil) return;
        try
        {
            var result = await _api.SubmitBarcodeAsync(barcode, _mode).ConfigureAwait(false);
            HandleAttendanceResult(result);
        }
        catch (Exception ex)
        {
            Log.Error("Gagal mengirim barcode", ex);
        }
    }

    private void HandleAttendanceResult(AttendanceResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (result.Success)
            {
                _cooldownUntil = DateTime.UtcNow.Add(CooldownAfterSuccess);
                StatusText.Text = $"Berhasil - {result.PersonName}";
                _sound.PlaySuccess();
                TambahRiwayat(result.PersonName, _mode);
            }
            else
            {
                StatusText.Text = result.Message ?? "Tidak dikenali.";

                // Wajah/identitas BERHASIL dikenali (server tahu ini siapa)
                // tapi presensinya ditolak krn alasan wajar (mis. "Sudah
                // presensi masuk hari ini") - BUKAN kegagalan pengenalan.
                // Tanpa cooldown di sini, org yang masih berdiri di depan
                // kamera bikin sistem coba scan ulang tiap <1 detik dan
                // pesan "Berhasil" yang tadi sempat tampil langsung
                // ketiban pesan ini - kelihatan spt gagal padahal barusan
                // sukses (ditemukan LANGSUNG dari laporan pengujian nyata,
                // data di database TETAP benar, ini murni bug tampilan).
                if (!string.IsNullOrEmpty(result.PersonName))
                {
                    _cooldownUntil = DateTime.UtcNow.Add(CooldownAfterSuccess);
                }
            }
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _modeTimer.Stop();
        _scannerReconnectTimer.Stop();
        _camera.Dispose();
        _scanner.Dispose();
        foreach (var f in _filters)
        {
            if (f is IDisposable d) d.Dispose();
        }
    }
}
