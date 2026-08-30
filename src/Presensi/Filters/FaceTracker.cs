using System.IO;
using System.Linq;
using DlibDotNet;
using OpenCvSharp;
using Presensi.Logging;

namespace Presensi.Filters;

/// <summary>
/// Deteksi wajah SATU pintu, dipakai bersama oleh semua FaceStickerFilter -
/// deteksi wajah (HOG, dlib) jauh lebih berat drpd sekadar resize+blend PNG
/// biasa, jadi DI-THROTTLE (lihat DetectInterval) & hasil terakhir di-cache,
/// bukan dijalankan ulang tiap frame preview (~12fps). Ini SELAIN
/// penghematan "preview mati = filter tidak jalan sama sekali" yang sudah
/// ada di CameraService - jadi dobel aman utk PC lawas.
///
/// PENTING (2026-08-31, perbaikan crash nyata dari pengujian user): versi
/// SEBELUMNYA memakai Dlib.LoadImageData(IntPtr, rows, cols, steps) lewat
/// pointer mentah Mat.Data - TERBUKTI bikin CRASH TOTAL aplikasi (Access
/// Violation 0xc0000005) krn asumsi urutan parameter/stride native yang
/// SALAH, dan celakanya native access violation itu TIDAK BISA ditangkap
/// try/catch C# biasa (bukan .NET exception, jadi Log.Error di bawah pun
/// tidak sempat jalan - aplikasi langsung mati). Diganti pakai jalur
/// encode/decode file PNG (Cv2.ImWrite + Dlib.LoadImage) - LEBIH LAMBAT
/// sedikit tapi PASTI BENAR krn lewat 2 codec yang sudah teruji matang,
/// tidak menebak-nebak layout memori mentah lagi.
/// </summary>
internal static class FaceTracker
{
    private static readonly FrontalFaceDetector Detector = Dlib.GetFrontalFaceDetector();
    private static readonly object Gate = new();
    private static readonly TimeSpan DetectInterval = TimeSpan.FromMilliseconds(200);
    private static readonly string TempImagePath = Path.Combine(Path.GetTempPath(), "presensi_face_detect.png");

    private static Rect? _lastBox;
    private static DateTime _lastDetectAt = DateTime.MinValue;

    /// <summary>Null kalau tidak ada wajah terdeteksi (belum ada orang di depan kamera / hasil cache terakhir juga kosong).</summary>
    public static Rect? GetFaceBox(Mat previewFrame)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastDetectAt < DetectInterval)
            {
                return _lastBox;
            }
            _lastDetectAt = DateTime.UtcNow;

            try
            {
                Cv2.ImWrite(TempImagePath, previewFrame);
                using var dlibImage = Dlib.LoadImage<RgbPixel>(TempImagePath);
                var faces = Detector.Operator(dlibImage);
                if (faces.Length == 0)
                {
                    _lastBox = null;
                    return null;
                }

                // Kiosk presensi cuma 1 orang di depan kamera pada satu waktu -
                // kalau kebetulan ada >1 wajah kepotret (org lewat di belakang),
                // prioritaskan yang PALING BESAR (paling dekat kamera).
                var best = faces.OrderByDescending(r => (long) r.Width * r.Height).First();
                _lastBox = new Rect(best.Left, best.Top, (int) best.Width, (int) best.Height);
                return _lastBox;
            }
            catch (Exception ex)
            {
                Log.Error("Gagal deteksi wajah utk filter stiker", ex);
                _lastBox = null;
                return null;
            }
        }
    }
}
