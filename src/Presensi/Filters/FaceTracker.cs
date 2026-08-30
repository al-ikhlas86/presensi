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
/// </summary>
internal static class FaceTracker
{
    private static readonly FrontalFaceDetector Detector = Dlib.GetFrontalFaceDetector();
    private static readonly object Gate = new();
    private static readonly TimeSpan DetectInterval = TimeSpan.FromMilliseconds(200);

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
                using var dlibImage = Dlib.LoadImageData<BgrPixel>(previewFrame.Data, (uint)previewFrame.Rows, (uint)previewFrame.Cols, (uint)previewFrame.Step());
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
