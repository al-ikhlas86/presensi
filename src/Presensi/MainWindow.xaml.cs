using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly PerangkatService _perangkat;

    private List<IPreviewFilter> _filters = new();
    private IPreviewFilter _activeFilter = new NoFilter();

    private readonly DispatcherTimer _modeTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    // Cek berkala apakah scanner yang sudah pernah dipasangkan (ScannerPort di
    // appsettings.json) masih/kembali tersambung - scanner USB kiosk kadang
    // lepas-colok sendiri seharian, bukan cuma soal nyambung sekali di awal.
    private readonly DispatcherTimer _scannerReconnectTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    // Scan kartu yang tertahan karena internet putus (disimpan di berkas lokal bersama waktu scan aslinya) dan pengirim ulangnya. Lihat AntreanOffline.
    private readonly AntreanOffline _antrean = new();
    private readonly DispatcherTimer _antreanTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _sedangKirimAntrean; // 0/1, dijaga Interlocked

    // Riwayat presensi live di panel kanan (diminta user 2026-09-01) - dibatasi
    // 100 baris terbaru, kiosk ini jalan berhari-hari tanpa restart jadi
    // daftarnya tidak boleh menumpuk tak terbatas (pola sama dgn Log.cs).
    private readonly ObservableCollection<string> _riwayatPresensi = new();
    private const int MaxRiwayat = 100;

    private AttendanceMode _mode = AttendanceMode.Masuk;
    private DateTime _lastIdentifyAttempt = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _busyFlag; // 0/1, dijaga Interlocked - cegah tumpang-tindih panggilan API

    // Selama jendela "Daftar Wajah" terbuka, frame kamera disalurkan ke sana
    // (dan presensi wajah dijeda supaya wajah siswa yang sedang didaftarkan tidak ikut dikenali).
    // Dibaca dari thread kamera, ditulis di UI thread -> volatile.
    private volatile Action<Mat>? _penerimaFrameMentah;
    private volatile Action<BitmapSource>? _penerimaPreview;

    // Dipakai TambahRiwayat menahan duplikat beruntun (lihat catatan di sana).
    private string? _lastRiwayatKey;
    private DateTime _lastRiwayatAt = DateTime.MinValue;
    private static readonly TimeSpan RiwayatDedupWindow = TimeSpan.FromSeconds(30);

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + AppVersionInfo.Current;
        Title = $"Presensi - Al-Ikhlas 86 (v{AppVersionInfo.Current})";

        _config = AppConfig.Load();
        // Pendaftaran mandiri perangkat ke server (Admin IT menyetujui di menu Perangkat > Absensi). Sebelum disetujui, token lama (bila ada)
        // tetap dipakai agar presensi tidak terputus; setelah disetujui otomatis beralih ke token perangkat.
        _perangkat = new PerangkatService(_config);
        _api = BuatKlienApi();
        _perangkat.StatusBerubah += (_, _) => Dispatcher.BeginInvoke(new Action(TerapkanStatusPerangkat));
        TerapkanStatusPerangkat();
        _perangkat.Mulai();
        ApplyDisplayMode();
        ApplyRiwayatPanelVisibility();

        FilterManager.EnsureSeeded();
        ReloadFilters();

        UpdateModeDisplay();
        _modeTimer.Tick += (_, _) => UpdateModeDisplay();
        _modeTimer.Start();

        RiwayatList.ItemsSource = _riwayatPresensi;

        // Banner "jangan tutup aplikasi" saat auto-update sedang mengunduh/
        // memasang (diminta tersirat user 2026-09-01 - berkali-kali menutup
        // app di tengah unduhan krn tidak ada tanda visual sama sekali
        // sebelumnya). UpdateService jalan di background thread, jadi
        // Dispatcher.Invoke WAJIB sebelum menyentuh elemen UI.
        Services.UpdateService.StatusChanged += (msg) => Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(msg))
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateBannerText.Text = msg;
                UpdateBanner.Visibility = Visibility.Visible;
            }
        });

        _camera.PreviewFrameCaptured += OnPreviewFrameCaptured;
        _camera.RawFrameCaptured += OnRawFrameCaptured;
        _camera.CameraError += (_, msg) => Dispatcher.Invoke(() => StatusText.Text = "Kamera bermasalah: " + msg);
        _scanner.BarcodeScanned += OnBarcodeScanned;
        _scanner.ScannerError += (_, msg) => Dispatcher.Invoke(() =>
        {
            StatusText.Text = "Scanner bermasalah: " + msg;
            UpdateScannerStatus();
        });

        // Otomatis sambung lagi ke scanner yang pernah dipasangkan, dicari lewat identitas USB-nya (bukan hanya nomor COM yang bisa berubah)
        // supaya PC yang dimatikan/dibuka lagi TIDAK perlu klik "Scanner..." manual (diminta user 2026-09-01, diperkuat 2026-11-28).
        TryReconnectScannerIfNeeded();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        UpdateAntreanChip();
        _antreanTimer.Tick += async (_, _) => await KirimAntreanAsync();
        _antreanTimer.Start();
        UpdateScannerStatus();
        _scannerReconnectTimer.Tick += (_, _) => TryReconnectScannerIfNeeded();
        _scannerReconnectTimer.Start();

        Loaded += (_, _) => _camera.Start(_config.CameraIndex);
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
            ToggleRiwayatButton.Content = "Sembunyikan Panel";
        }
        else
        {
            RiwayatSpacerColumn.Width = new GridLength(0);
            RiwayatPanelColumn.Width = new GridLength(0);
            RiwayatPanelBorder.Visibility = Visibility.Collapsed;
            ToggleRiwayatButton.Content = "Tampilkan Panel";
        }
    }

    // Tombol langsung di layar utama (diminta user 2026-09-01: "sediakan
    // tutup dan buka... jangan sampe kebuka terus") - SEBELUMNYA cuma bisa
    // diatur lewat dialog Pengaturan (perlu buka dialog dulu tiap ganti),
    // sekarang 1 klik langsung tutup/buka, dan pilihannya disimpan supaya
    // diingat di buka berikutnya juga.
    private void ToggleRiwayatButton_Click(object sender, RoutedEventArgs e)
    {
        _config.ShowRiwayatPanel = !_config.ShowRiwayatPanel;
        AppConfig.Save(_config);
        ApplyRiwayatPanelVisibility();
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
        // Kabel dicabut: handle port lama masih "terbuka" tetapi mati (scan tidak masuk, tanpa tanda apa pun). Buang agar bisa menyambung ulang.
        if (_scanner.IsConnected && !_scanner.PortMasihAda) _scanner.Disconnect();
        if (_scanner.IsConnected) return;
        if (string.IsNullOrWhiteSpace(_config.ScannerPort) && string.IsNullOrWhiteSpace(_config.ScannerDeviceId)) { UpdateScannerStatus(); return; }
        var port = ScannerPortFinder.CariPort(_config.ScannerDeviceId, _config.ScannerPort);
        if (port != null)
        {
            _scanner.Connect(port);
            if (_scanner.IsConnected && !string.Equals(port, _config.ScannerPort, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"[Scanner] scanner pindah port: {_config.ScannerPort} -> {port}");
                _config.ScannerPort = port; // nomor COM berubah tetapi perangkatnya sama
                AppConfig.Save(_config);
            }
        }
        UpdateScannerStatus();
    }

    /// <summary>Setelah PC bangun dari tidur, handle serial lama biasanya mati: tutup, lalu biarkan timer menyambung ulang.</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scanner.Disconnect();
            TryReconnectScannerIfNeeded();
        }));
    }

    private void UpdateScannerStatus()
    {
        var belumPernah = string.IsNullOrWhiteSpace(_config.ScannerPort) && string.IsNullOrWhiteSpace(_config.ScannerDeviceId);
        ScannerStatusText.Text = _scanner.IsConnected
            ? $"Scanner: tersambung ({_config.ScannerPort})"
            : belumPernah ? "Scanner: belum dipasangkan" : "Scanner: tidak terdeteksi (cek kabel USB)";
        // Penanda selalu terlihat di judul (panel kanan bisa disembunyikan): petugas tahu scanner kartu siap atau tidak sebelum antrean datang.
        ScannerChip.Text = _scanner.IsConnected ? $"\u25CF Scanner kartu siap ({_config.ScannerPort})" : belumPernah ? "\u25CF Scanner kartu belum dipasangkan" : "\u25CF Scanner kartu TIDAK TERSAMBUNG";
        ScannerChipBorder.Background = new SolidColorBrush(_scanner.IsConnected ? Color.FromRgb(0x15, 0x80, 0x3D) : belumPernah ? Color.FromRgb(0x4B, 0x55, 0x63) : Color.FromRgb(0xB9, 0x1C, 0x1C));
    }

    private void TambahRiwayat(string? nama, AttendanceMode mode)
    {
        if (string.IsNullOrWhiteSpace(nama)) return;

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

    /// <summary>Pilih klien API sesuai status perangkat (lihat PerangkatService). Dipanggil di UI thread.</summary>
    private IAttendanceApiClient BuatKlienApi()
    {
        var tokenPerangkat = _perangkat.TokenPerangkat;
        if (!string.IsNullOrWhiteSpace(tokenPerangkat))
            return new AbsenAttendanceApiClient { BaseUrl = _config.BaseUrl, KioskToken = tokenPerangkat };
        // Diblokir/dicabut Admin IT: perangkat ini TIDAK boleh lagi memakai token lama bersama.
        if (_perangkat.Terlarang) return new MenungguPersetujuanClient(_perangkat.Pesan);
        // Masih menunggu persetujuan: instalasi lama tetap memakai token lamanya (presensi tidak terputus).
        if (!string.IsNullOrWhiteSpace(_config.KioskToken))
            return new AbsenAttendanceApiClient { BaseUrl = _config.BaseUrl, KioskToken = _config.KioskToken };
        // Instalasi baru tanpa token: tidak ada hasil palsu (mode uji) - tampilkan status pendaftaran.
        return new MenungguPersetujuanClient(_perangkat.Pesan);
    }

    private void TerapkanStatusPerangkat()
    {
        _api = BuatKlienApi();
        var status = _perangkat.Status;
        if (status == StatusPerangkat.Aktif || status == StatusPerangkat.Belum)
        {
            PerangkatBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var masihJalan = !_perangkat.Terlarang && !string.IsNullOrWhiteSpace(_config.KioskToken) && status != StatusPerangkat.Aktif;
        PerangkatBannerText.Text = masihJalan ? _perangkat.Pesan() + " Presensi tetap berjalan sementara." : _perangkat.Pesan();
        PerangkatBanner.Visibility = Visibility.Visible;
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

    private void DaftarWajahButton_Click(object sender, RoutedEventArgs e)
    {
        var token = _perangkat.TokenPerangkat;
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(this, "Perangkat ini belum disetujui Admin IT (belum punya token perangkat). Pendaftaran wajah baru bisa dipakai setelah perangkat aktif.", "Daftar Wajah", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var klien = new WajahApiClient(_config.BaseUrl, token);
        var jendela = new DaftarWajahWindow(klien) { Owner = this };
        _penerimaFrameMentah = jendela.TerimaFrameMentah;
        _penerimaPreview = jendela.TerimaPreview;
        try { jendela.ShowDialog(); }
        finally
        {
            _penerimaFrameMentah = null;
            _penerimaPreview = null;
        }
    }

    private void ScannerButton_Click(object sender, RoutedEventArgs e)
    {
        var daftar = ScannerPortFinder.Daftar();
        if (daftar.Count == 0)
        {
            MessageBox.Show(this, "Tidak ada port serial terdeteksi. Pastikan scanner sudah tersambung ke kabel USB.", "Scanner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Scanner mode CDC muncul sebagai port USB; COM bawaan PC (tanpa identitas USB) disisihkan bila ada pilihan USB.
        var usb = daftar.Where(p => p.VidPid != null).ToList();
        var kandidat = usb.Count > 0 ? usb : daftar.ToList();
        var pilihan = kandidat.Count == 1 ? kandidat[0] : new PilihPortWindow(kandidat) { Owner = this }.Tampilkan();
        if (pilihan == null) return;
        _scanner.Connect(pilihan.PortName);
        if (_scanner.IsConnected)
        {
            // Disimpan supaya kali berikutnya PC dinyalakan/app dibuka lagi, scanner ini otomatis tersambung sendiri walau nomor COM-nya berubah.
            _config.ScannerPort = pilihan.PortName;
            _config.ScannerDeviceId = pilihan.VidPid;
            AppConfig.Save(_config);
        }
        UpdateScannerStatus();
        MessageBox.Show(this, _scanner.IsConnected ? $"Tersambung ke {pilihan.PortName}." : "Gagal tersambung. Pastikan tidak ada program lain yang memakai scanner ini.", "Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _penerimaPreview?.Invoke(bitmap);
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
            var penerima = _penerimaFrameMentah;
            if (penerima != null)
            {
                penerima(frame); // mode pendaftaran wajah: presensi wajah dijeda, frame hanya untuk jendela pendaftaran
                return;
            }
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

    // Kartu discan bergiliran oleh banyak anak: SATU jeda global (seperti pada wajah) membuang scan kartu berikutnya diam-diam. Yang ditahan hanya kode yang SAMA
    // dalam beberapa detik (kartu yang terbaca dua kali); kartu lain langsung diproses. Gagal terkirim/tidak terdaftar = kode boleh discan lagi.
    private static readonly TimeSpan BarcodeDedup = TimeSpan.FromSeconds(4);
    private readonly Dictionary<string, DateTime> _barcodeTerakhir = new();

    private bool TahanBarcodeGanda(string barcode)
    {
        lock (_barcodeTerakhir)
        {
            var sekarang = DateTime.UtcNow;
            if (_barcodeTerakhir.TryGetValue(barcode, out var t) && sekarang - t < BarcodeDedup) return true;
            if (_barcodeTerakhir.Count > 200)
            {
                foreach (var k in _barcodeTerakhir.Where(x => sekarang - x.Value >= BarcodeDedup).Select(x => x.Key).ToList()) _barcodeTerakhir.Remove(k);
            }
            _barcodeTerakhir[barcode] = sekarang;
            return false;
        }
    }

    private void LupakanBarcode(string barcode)
    {
        lock (_barcodeTerakhir) _barcodeTerakhir.Remove(barcode);
    }

    private async void OnBarcodeScanned(object? sender, string barcode)
    {
        if (_penerimaFrameMentah != null) return; // dijeda selama pendaftaran wajah
        if (TahanBarcodeGanda(barcode)) return;
        var waktuScan = DateTime.UtcNow; // waktu asli scan: dipakai server walau terkirimnya nanti (antrean offline)
        var modeScan = _mode;
        try
        {
            var result = await _api.SubmitBarcodeAsync(barcode, modeScan).ConfigureAwait(false);
            if (result.GagalJaringan)
            {
                // Internet putus/server tidak terjangkau: scan disimpan dan dikirim otomatis nanti, BUKAN dibuang atau dianggap ditolak.
                _antrean.Tambah(barcode, modeScan, waktuScan);
                Log.Warn($"[Antrean] kartu {barcode} disimpan offline ({_antrean.Jumlah} menunggu)");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateAntreanChip();
                    ShowResultOverlay($"Kartu {barcode}", "Tersimpan (offline). Dikirim otomatis saat internet tersambung.", true);
                    _sound.PlaySuccess();
                }));
                return;
            }
            HandleAttendanceResult(result);
            if (!result.Success && string.IsNullOrEmpty(result.PersonName))
            {
                LupakanBarcode(barcode);
                Dispatcher.BeginInvoke(new Action(() => ShowResultOverlay($"Kartu {barcode}", result.Message ?? "Tidak dikenali.", false, gagal: true)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Gagal mengirim barcode", ex);
            LupakanBarcode(barcode);
            Dispatcher.BeginInvoke(new Action(() => ShowResultOverlay("Gagal mengirim", "Periksa koneksi internet lalu scan kartu lagi.", false, gagal: true)));
        }
    }

    private void UpdateAntreanChip()
    {
        var n = _antrean.Jumlah;
        AntreanChip.Text = $"\u25CF Offline: {n} scan menunggu dikirim";
        AntreanChipBorder.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mengirim ulang scan offline berurutan (tertua dulu) dengan waktu aslinya. Berhenti di kegagalan jaringan pertama; jawaban server apa pun (tercatat/ditolak) = selesai untuk entri itu.</summary>
    private async Task KirimAntreanAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _sedangKirimAntrean, 1) == 1) return;
        try
        {
            if (_antrean.Jumlah == 0 || _api is MenungguPersetujuanClient) return;
            _antrean.BuangKedaluwarsa();
            for (var i = 0; i < 100; i++)
            {
                var entri = _antrean.Tertua();
                if (entri == null) break;
                var mode = entri.Mode == "pulang" ? AttendanceMode.Pulang : AttendanceMode.Masuk;
                var hasil = await _api.SubmitBarcodeAsync(entri.Kode, mode, entri.WaktuUtc).ConfigureAwait(false);
                if (hasil.GagalJaringan) break; // masih offline: urutan dipertahankan, coba lagi 30 dtk lagi
                _antrean.Hapus(entri);
                Log.Info(hasil.Success
                    ? $"[Antrean] kartu {entri.Kode} ({entri.Mode}, {entri.WaktuUtc:u}) terkirim"
                    : $"[Antrean] kartu {entri.Kode} ({entri.Mode}, {entri.WaktuUtc:u}) dijawab server: {hasil.Message} - dihapus dari antrean");
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Antrean] gagal mengirim ulang scan offline", ex);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _sedangKirimAntrean, 0);
            Dispatcher.BeginInvoke(new Action(UpdateAntreanChip));
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
                ShowResultOverlay(result.PersonName, _mode == AttendanceMode.Masuk ? "Presensi MASUK tercatat" : "Presensi PULANG tercatat", true);
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
                    ShowResultOverlay(result.PersonName, result.Message ?? "Sudah tercatat sebelumnya", false);
                }
            }
        });
    }

    // ------------------------------------------------------------------
    // Lapisan hasil presensi: centang besar + nama (diminta user 2026-10-23,
    // anak-anak absen ramai-ramai dan perlu tahu langsung kalau sudah masuk).
    // Dipanggil dari UI thread (di dalam Dispatcher.BeginInvoke). Menampilkan
    // beberapa detik lalu menghilang sendiri; hasil baru menimpa yang lama.
    // ------------------------------------------------------------------
    private static readonly TimeSpan ResultOverlayDuration = TimeSpan.FromSeconds(2.5);
    private readonly DispatcherTimer _resultOverlayTimer = new();

    private void ShowResultOverlay(string? nama, string detail, bool tercatat, bool gagal = false)
    {
        try
        {
            ResultName.Text = string.IsNullOrWhiteSpace(nama) ? "" : nama;
            ResultDetail.Text = detail;
            ResultCircle.Fill = new SolidColorBrush(gagal
                ? System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)   // merah: gagal / tidak terdaftar
                : tercatat
                    ? System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)   // hijau
                    : System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // kuning: sudah tercatat
            ResultMark.Data = Geometry.Parse(gagal ? "M 62,62 L 138,138 M 138,62 L 62,138" : "M 55,105 L 88,138 L 148,66");

            ResultOverlay.Visibility = Visibility.Visible;
            ResultOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            ResultScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ResultScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

            _resultOverlayTimer.Stop();
            _resultOverlayTimer.Interval = ResultOverlayDuration;
            _resultOverlayTimer.Tick -= ResultOverlayTimer_Tick;
            _resultOverlayTimer.Tick += ResultOverlayTimer_Tick;
            _resultOverlayTimer.Start();
        }
        catch (Exception ex)
        {
            // Tampilan hanya pemanis - kegagalan di sini tidak boleh mengganggu pencatatan presensi.
            Log.Error("Gagal menampilkan lapisan hasil presensi", ex);
        }
    }

    private void ResultOverlayTimer_Tick(object? sender, EventArgs e)
    {
        _resultOverlayTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => ResultOverlay.Visibility = Visibility.Collapsed;
        ResultOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _perangkat.Dispose();
        _modeTimer.Stop();
        _scannerReconnectTimer.Stop();
        _antreanTimer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _resultOverlayTimer.Stop();
        _camera.Dispose();
        _scanner.Dispose();
        foreach (var f in _filters)
        {
            if (f is IDisposable d) d.Dispose();
        }

        // WAJIB eksplisit - App.xaml.cs sekarang pakai ShutdownMode.
        // OnExplicitShutdown (bukan bawaan OnLastWindowClose), supaya
        // popup update di awal startup bisa ditutup lalu diganti MainWindow
        // tanpa app ikut keluar sendiri di antaranya. Konsekuensinya: kalau
        // MainWindow ditutup (tombol X) TANPA baris ini, app akan terus
        // "hidup" tanpa jendela apa pun (proses nyangkut, tidak pernah
        // benar2 keluar) - jadi penutupan window utama HARUS memicu
        // Shutdown() aplikasi secara manual di sini.
        Application.Current.Shutdown();
    }
}
