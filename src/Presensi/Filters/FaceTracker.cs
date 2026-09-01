using System.IO;
using System.Linq;
using DlibDotNet;
using OpenCvSharp;
using Presensi.Logging;
// DlibDotNet & OpenCvSharp SAMA-SAMA punya tipe "Point" - dipakai bersamaan
// di file ini (landmark dari Dlib, hasil akhir dalam koordinat OpenCvSharp
// utk dipakai FaceStickerFilter), jadi hasil landmark WAJIB dialiaskan
// eksplisit ke OpenCvSharp.Point.
using CvPoint = OpenCvSharp.Point;

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
///
/// 2026-09-01: 1 siklus deteksi SEKARANG opsional sekalian ambil 68 titik
/// landmark wajah (net8.0-windows saja, lihat LandmarkModelService) di ATAS
/// kotak wajah yang sudah terdeteksi - BUKAN 2 pintu/siklus terpisah, supaya
/// tetap 1x throttle & 1x baca file temp yang sama utk keduanya.
/// </summary>
internal static class FaceTracker
{
    private static readonly FrontalFaceDetector Detector = Dlib.GetFrontalFaceDetector();
    private static readonly object Gate = new();
    private static readonly TimeSpan DetectInterval = TimeSpan.FromMilliseconds(200);
    private static readonly string TempImagePath = Path.Combine(Path.GetTempPath(), "presensi_face_detect.png");

    private static Rect? _lastBox;
#if !NET48
    private static CvPoint[]? _lastLandmarks;
#endif
    private static DateTime _lastDetectAt = DateTime.MinValue;

    /// <summary>Null kalau tidak ada wajah terdeteksi (belum ada orang di depan kamera / hasil cache terakhir juga kosong).</summary>
    public static Rect? GetFaceBox(Mat previewFrame)
    {
        DetectIfNeeded(previewFrame);
        return _lastBox;
    }

#if !NET48
    /// <summary>
    /// 68 titik wajah urutan standar dlib (0-16 garis rahang, 17-26 alis,
    /// 27-35 hidung, 36-41 mata kanan subjek, 42-47 mata kiri subjek, 48-67
    /// mulut) - null kalau model belum siap (LandmarkModelService.Predictor
    /// masih null, lihat MainWindow.xaml.cs pemanggilan EnsureLoadedAsync)
    /// ATAU tidak ada wajah terdeteksi.
    /// </summary>
    public static CvPoint[]? GetFaceLandmarks(Mat previewFrame)
    {
        DetectIfNeeded(previewFrame);
        return _lastLandmarks;
    }
#endif

    private static void DetectIfNeeded(Mat previewFrame)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastDetectAt < DetectInterval) return;
            _lastDetectAt = DateTime.UtcNow;

            try
            {
                Cv2.ImWrite(TempImagePath, previewFrame);
                using var dlibImage = Dlib.LoadImage<RgbPixel>(TempImagePath);
                var faces = Detector.Operator(dlibImage);
                if (faces.Length == 0)
                {
                    _lastBox = null;
#if !NET48
                    _lastLandmarks = null;
#endif
                    return;
                }

                // Kiosk presensi cuma 1 orang di depan kamera pada satu waktu -
                // kalau kebetulan ada >1 wajah kepotret (org lewat di belakang),
                // prioritaskan yang PALING BESAR (paling dekat kamera).
                var best = faces.OrderByDescending(r => (long) r.Width * r.Height).First();
                _lastBox = new Rect(best.Left, best.Top, (int) best.Width, (int) best.Height);

#if !NET48
                _lastLandmarks = null;
                var predictor = LandmarkModelService.Predictor;
                if (predictor is not null)
                {
                    using var shape = predictor.Detect(dlibImage, best);
                    var points = new CvPoint[shape.Parts];
                    for (uint i = 0; i < shape.Parts; i++)
                    {
                        var p = shape.GetPart(i);
                        points[i] = new CvPoint(p.X, p.Y);
                    }
                    _lastLandmarks = points;
                }
#endif
            }
            catch (Exception ex)
            {
                Log.Error("Gagal deteksi wajah utk filter stiker", ex);
                _lastBox = null;
#if !NET48
                _lastLandmarks = null;
#endif
            }
        }
    }
}
