using OpenCvSharp;

namespace Presensi.Services;

/// <summary>
/// Kamera capture SELALU jalan di latar belakang begitu Start() dipanggil,
/// TIDAK PERNAH berhenti gara-gara preview dimatikan (lihat PreviewEnabled)
/// - inilah inti permintaan user: "meski preview mati/user lagi buka
/// Excel, siapapun yang scan wajah di kamera tetap terhitung masuk".
/// </summary>
public interface ICameraService : IDisposable
{
    bool IsRunning { get; }

    /// <summary>
    /// true = frame juga dikonversi & dikirim ke UI (mahal, ada biaya CPU).
    /// false = capture &amp; deteksi wajah TETAP jalan penuh, cuma rendering
    /// ke layar yang dilewati - inilah cara mengoptimalkan PC yang lag.
    /// </summary>
    bool PreviewEnabled { get; set; }

    /// <summary>
    /// Frame MENTAH, tiap frame, TANPA filter apa pun - satu-satunya yang
    /// boleh dikirim ke pipeline pengenalan wajah/API. Filter dekoratif
    /// TIDAK PERNAH boleh menyentuh event ini.
    /// </summary>
    event EventHandler<Mat>? RawFrameCaptured;

    /// <summary>
    /// Frame siap-tampil (BGR->BGRA, sudah di-flip utk mirror-preview) -
    /// cuma diisi kalau PreviewEnabled true. Filter dekoratif ditumpuk
    /// SETELAH event ini di MainWindow, bukan di sini.
    /// </summary>
    event EventHandler<Mat>? PreviewFrameCaptured;

    event EventHandler<string>? CameraError;

    void Start(int cameraIndex = 0);
    void Stop();
}
