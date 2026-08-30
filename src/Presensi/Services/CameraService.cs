using OpenCvSharp;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Implementasi ICameraService pakai OpenCvSharp - dipilih krn satu-satunya
/// library kamera yang terbukti kompatibel dgn KEDUA target build (net48 &amp;
/// net8.0-windows) dari 1 kode yang sama, jadi tidak perlu logic kamera
/// bercabang per-runtime.
///
/// FPS DIBATASI SENGAJA (lihat CaptureIntervalMs) - kamera 30/60fps native
/// tidak dibutuhkan sama sekali utk deteksi wajah/barcode, cuma buang-buang
/// CPU/daya (persis kekhawatiran user "PC TU jangan sampai lag"). ~12fps
/// sudah lebih dari cukup utk mata manusia terasa "hidup" di preview DAN
/// utk pipeline pengenalan wajah (yang sendiri baru proses tiap beberapa
/// ratus ms, lihat pola polling di web Absen yang sudah terbukti - 300-600ms
/// per cek, bukan tiap frame).
/// </summary>
public sealed class CameraService : ICameraService
{
    private const int CaptureIntervalMs = 80; // ~12 fps

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning { get; private set; }
    public bool PreviewEnabled { get; set; } = true;

    public event EventHandler<Mat>? RawFrameCaptured;
    public event EventHandler<Mat>? PreviewFrameCaptured;
    public event EventHandler<string>? CameraError;

    public void Start(int cameraIndex = 0)
    {
        if (IsRunning) return;

        try
        {
            // CAP_DSHOW dipaksa eksplisit (bukan biarkan OpenCV pilih
            // backend default) - DirectShow adalah backend kamera paling
            // kompatibel di Windows LAMA (7) sekaligus tetap didukung penuh
            // di Windows 10/11, beda dari MSMF yang cuma ada di Windows 10+.
            // Krn target build ini MEMANG termasuk Windows 7, backend harus
            // yang paling luas dukungannya, bukan yang paling baru.
            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                CameraError?.Invoke(this, $"Tidak bisa membuka kamera index {cameraIndex}.");
                _capture.Dispose();
                _capture = null;
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Gagal membuka kamera", ex);
            CameraError?.Invoke(this, "Gagal membuka kamera: " + ex.Message);
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _loopTask = Task.Run(() => CaptureLoop(_cts.Token));
        Log.Info($"Kamera index {cameraIndex} dimulai.");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* abaikan, ini shutdown path */ }
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        Log.Info("Kamera dihentikan.");
    }

    private void CaptureLoop(CancellationToken token)
    {
        using var frame = new Mat();
        var consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_capture is null || !_capture.Read(frame) || frame.Empty())
                {
                    consecutiveFailures++;
                    // Kamera lepas-colok/USB goyang itu NYATA di lapangan
                    // (PC sekolah, bukan lab bersih) - jangan langsung
                    // menyerah di percobaan gagal pertama.
                    if (consecutiveFailures > 30)
                    {
                        CameraError?.Invoke(this, "Kamera tidak merespons - cek koneksi kabel/USB.");
                        consecutiveFailures = 0;
                    }
                    Thread.Sleep(CaptureIntervalMs);
                    continue;
                }
                consecutiveFailures = 0;

                // Frame MENTAH dulu ke konsumen pengenalan wajah - SEBELUM
                // preview/filter apa pun menyentuhnya.
                using (var rawClone = frame.Clone())
                {
                    RawFrameCaptured?.Invoke(this, rawClone);
                }

                // Konversi ke bentuk siap-tampil cuma kalau preview MEMANG
                // dinyalakan - inilah penghematan CPU yang diminta user.
                if (PreviewEnabled)
                {
                    using var mirrored = new Mat();
                    Cv2.Flip(frame, mirrored, FlipMode.Y); // mirror, spt kaca - alami utk kiosk swafoto
                    using var previewClone = mirrored.Clone();
                    PreviewFrameCaptured?.Invoke(this, previewClone);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error di capture loop", ex);
            }

            Thread.Sleep(CaptureIntervalMs);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
