using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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
    private readonly IAttendanceApiClient _api = new StubAttendanceApiClient(); // lihat catatan di class-nya
    private readonly SoundService _sound = new();

    private readonly IPreviewFilter[] _filters =
    {
        new NoFilter(),
        ThemedBorderFilter.Kemerdekaan(),
        ThemedBorderFilter.Ramadan(),
    };
    private IPreviewFilter _activeFilter = new NoFilter();

    private AttendanceMode _mode = AttendanceMode.Masuk;
    private DateTime _lastIdentifyAttempt = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _busyFlag; // 0/1, dijaga Interlocked - cegah tumpang-tindih panggilan API

    public MainWindow()
    {
        InitializeComponent();

        FilterCombo.ItemsSource = _filters;
        FilterCombo.DisplayMemberPath = nameof(IPreviewFilter.DisplayName);
        FilterCombo.SelectedIndex = 0;

        _camera.PreviewFrameCaptured += OnPreviewFrameCaptured;
        _camera.RawFrameCaptured += OnRawFrameCaptured;
        _camera.CameraError += (_, msg) => Dispatcher.Invoke(() => StatusText.Text = "Kamera bermasalah: " + msg);
        _scanner.BarcodeScanned += OnBarcodeScanned;
        _scanner.ScannerError += (_, msg) => Dispatcher.Invoke(() => StatusText.Text = "Scanner bermasalah: " + msg);

        Loaded += (_, _) => _camera.Start();
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        _mode = RbMasuk.IsChecked == true ? AttendanceMode.Masuk : AttendanceMode.Pulang;
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
            }
            else
            {
                StatusText.Text = result.Message ?? "Tidak dikenali.";
            }
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _camera.Dispose();
        _scanner.Dispose();
    }
}
