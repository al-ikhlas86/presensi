using System.IO;
using System.IO.Compression;
using System.Text.Json;
using OpenCvSharp;
using Presensi.Logging;

namespace Presensi.Filters;

/// <summary>
/// Filter "nempel di wajah" (astronot, kacamata, dst) - beda dari
/// ImageOverlayFilter (border/bingkai statis full-frame): stiker ini
/// mengikuti POSISI &amp; UKURAN wajah yang terdeteksi (lihat FaceTracker),
/// bukan ditempel diam di 1 titik layar. Placement PENDEKATAN (dari kotak
/// wajah + rasio di manifest), BUKAN landmark 68-titik presisi - cukup
/// utk helm/kacamata/telinga, tidak ikut miring persis saat kepala miring.
///
/// Format bundle: file ".stiker" = ZIP berisi manifest.json + sticker.png
/// (PNG transparan) - sengaja BUKAN file .png polos spt ImageOverlayFilter,
/// krn stiker wajah butuh info penempatan (lihat StickerManifest), bukan
/// cuma gambar mentah.
/// </summary>
public sealed class FaceStickerFilter : IPreviewFilter, IDisposable
{
    public string DisplayName { get; }
    public bool IsDeletable => true;
    public string? FilePath { get; }

    private readonly StickerManifest _manifest;
    private readonly Mat _stickerBgra;

    public FaceStickerFilter(string filePath)
    {
        FilePath = filePath;

        using var zip = ZipFile.OpenRead(filePath);
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Bundle stiker tidak punya manifest.json: " + filePath);
        var stickerEntry = zip.GetEntry("sticker.png")
            ?? throw new InvalidOperationException("Bundle stiker tidak punya sticker.png: " + filePath);

        using (var stream = manifestEntry.Open())
        {
            _manifest = JsonSerializer.Deserialize<StickerManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new StickerManifest();
        }
        DisplayName = _manifest.DisplayName;

        using (var stream = stickerEntry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            _stickerBgra = Cv2.ImDecode(memory.ToArray(), ImreadModes.Unchanged);
        }
        if (_stickerBgra.Empty())
        {
            throw new InvalidOperationException("Gagal membaca sticker.png di dalam bundle: " + filePath);
        }
        if (_stickerBgra.Channels() == 3)
        {
            Cv2.CvtColor(_stickerBgra, _stickerBgra, ColorConversionCodes.BGR2BGRA);
        }
    }

    public void Apply(Mat previewFrame)
    {
#if !NET48
        // Model landmark (~64MB) SENGAJA baru mulai diunduh DI SINI (saat
        // stiker ini benar2 dipilih & lagi dirender), BUKAN di startup app
        // spt versi sebelumnya - diminta user 2026-09-01 ("jangan sampe
        // sistem bikin berat") - PC yang operatornya tidak pernah pilih
        // filter stiker (mis. tetap "Normal"/filter gambar biasa) TIDAK
        // PERNAH mengunduh model ini sama sekali. EnsureLoadedAsync()
        // idempotent (aman dipanggil tiap frame, cuma proses beneran 1x).
        _ = LandmarkModelService.EnsureLoadedAsync();
#endif
        var faceBox = FaceTracker.GetFaceBox(previewFrame);
        if (faceBox is null) return; // Tidak ada wajah terdeteksi - jangan gambar apa pun drpd stiker "melayang" sembarangan.

        try
        {
            var box = faceBox.Value;
#if !NET48
            if (_manifest.SkinSmooth) ApplySkinSmooth(previewFrame, box);
#endif
            // Skala SENGAJA TETAP dari kotak wajah apa adanya (bukan basis
            // landmark) - box.Width sudah teruji stabil, sedangkan skala
            // berbasis landmark (mis. lebar rahang) butuh WidthRatio ditata
            // ulang manual tiap stiker yang belum bisa diverifikasi visual
            // langsung di sesi ini.
            var stickerWidth = (int) (box.Width * _manifest.WidthRatio);
            var aspectRatio = (double) _stickerBgra.Rows / _stickerBgra.Cols;
            var stickerHeight = (int) (stickerWidth * aspectRatio);
            if (stickerWidth <= 0 || stickerHeight <= 0) return;

            // Titik jangkar DASAR - "box" (bawaan/fallback) dari kotak wajah
            // kasar, atau presisi dari landmark (mata/alis) kalau model
            // sudah siap & manifest memintanya (lihat StickerManifest.
            // AnchorLandmark). AnchorYRatio/OffsetXRatio tetap dipakai sbg
            // pergeseran halus DI ATAS titik dasar ini, bukan diganti.
            double anchorX = box.X + box.Width / 2.0;
            double anchorY = box.Y;

            double rotationDegrees = 0;
#if !NET48
            // Landmark (diminta user 2026-09-01, "kualitas kelas Instagram/
            // TikTok") dipakai utk DUA hal: (1) titik jangkar presisi kalau
            // manifest memintanya, (2) sudut kemiringan kepala - stiker ikut
            // miring mengikuti kepala, bukan nempel lurus terus.
            var landmarks = FaceTracker.GetFaceLandmarks(previewFrame);
            if (landmarks is { Length: 68 })
            {
                var rightEye = AverageOf(landmarks, 36, 37, 38, 39, 40, 41);
                var leftEye = AverageOf(landmarks, 42, 43, 44, 45, 46, 47);
                rotationDegrees = Math.Atan2(leftEye.Y - rightEye.Y, leftEye.X - rightEye.X) * 180.0 / Math.PI;

                switch (_manifest.AnchorLandmark)
                {
                    case "eyes":
                        var eyeMid = AverageOf(landmarks, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47);
                        anchorX = eyeMid.X;
                        anchorY = eyeMid.Y;
                        break;
                    case "eyebrows":
                        var browMid = AverageOf(landmarks, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26);
                        anchorX = browMid.X;
                        anchorY = browMid.Y;
                        break;
                    // "box" (atau nilai lain yang tidak dikenal) - biarkan
                    // anchorX/anchorY dari kotak wajah di atas, tidak diubah.
                }
            }
#endif

            var centerX = anchorX + box.Width * _manifest.OffsetXRatio;
            var centerY = anchorY + box.Height * _manifest.AnchorYRatio;

            var destX = (int) (centerX - stickerWidth / 2.0);
            var destY = (int) (centerY - stickerHeight / 2.0);

            DrawClipped(previewFrame, destX, destY, stickerWidth, stickerHeight, rotationDegrees);
        }
        catch (Exception ex)
        {
            Log.Error("Gagal menerapkan filter stiker: " + DisplayName, ex);
        }
    }

#if !NET48
    private static Point2f AverageOf(Point[] points, params int[] indices)
    {
        double sx = 0, sy = 0;
        foreach (var i in indices) { sx += points[i].X; sy += points[i].Y; }
        return new Point2f((float) (sx / indices.Length), (float) (sy / indices.Length));
    }

    // "Glow" kulit halus (diminta user 2026-09-01, "presisi & menghibur kaya
    // Instagram/TikTok") - bilateral filter MENGHALUSKAN TEKSTUR kulit tanpa
    // mengaburkan garis wajah/mata/mulut (beda dari blur/GaussianBlur biasa
    // yang mengaburkan SEMUANYA rata) - teknik yang sama dipakai beauty
    // filter komersial. Area diperbesar 15% dari kotak wajah supaya transisi
    // ke dahi/leher tidak terlalu tajam. KHUSUS net8.0-windows (dipanggil
    // hanya dari dalam #if !NET48 di Apply()).
    private static void ApplySkinSmooth(Mat frame, Rect box)
    {
        var expand = (int) (box.Width * 0.15);
        var x = Math.Max(0, box.X - expand);
        var y = Math.Max(0, box.Y - expand);
        var width = Math.Min(frame.Cols - x, box.Width + expand * 2);
        var height = Math.Min(frame.Rows - y, box.Height + expand * 2);
        if (width <= 0 || height <= 0) return;

        using var faceRegion = new Mat(frame, new Rect(x, y, width, height));
        using var smoothed = new Mat();
        // d=9, sigmaColor=50, sigmaSpace=50 - nilai umum "beauty filter"
        // ringan: cukup halus tapi TIDAK menghilangkan detail wajah sama
        // sekali (belum diverifikasi dampak performa di kamera sungguhan -
        // perlu diamati saat uji nyata, lihat catatan di README).
        Cv2.BilateralFilter(faceRegion, smoothed, 9, 50, 50);
        smoothed.CopyTo(faceRegion);
    }
#endif

    private void DrawClipped(Mat frame, int destX, int destY, int width, int height, double rotationDegrees = 0)
    {
        // Stiker bisa sebagian keluar batas layar (mis. helm astronot yang
        // menjulur ke atas saat orang berdiri dekat kamera) - potong dulu
        // ke area yang benar-benar overlap dgn frame sebelum blend, drpd
        // exception index-out-of-range.
        var srcRect = new Rect(0, 0, width, height);
        var dstRectOnFrame = new Rect(destX, destY, width, height);
        var frameRect = new Rect(0, 0, frame.Cols, frame.Rows);
        var visible = dstRectOnFrame.Intersect(frameRect);
        if (visible.Width <= 0 || visible.Height <= 0) return;

        var srcX = visible.X - destX;
        var srcY = visible.Y - destY;

        using var resizedSticker = new Mat();
        Cv2.Resize(_stickerBgra, resizedSticker, new Size(width, height), 0, 0, InterpolationFlags.Linear);

        // Rotasi opsional (net8.0-windows + model landmark siap saja, lihat
        // Apply()) - diputar di sekitar TITIK TENGAH stiker sendiri, area di
        // luar batas hasil rotasi diisi alpha=0 (Scalar.All(0), transparan
        // penuh) supaya sudut kosong bekas rotasi tidak kelihatan sbg kotak
        // solid. Ambang 0.5 derajat cuma hindari kerja WarpAffine sia-sia
        // saat kepala nyaris tegak sempurna.
        Mat? rotated = null;
        var stickerToBlend = resizedSticker;
        if (Math.Abs(rotationDegrees) > 0.5)
        {
            rotated = new Mat();
            var center = new Point2f(width / 2f, height / 2f);
            var rotMatrix = Cv2.GetRotationMatrix2D(center, -rotationDegrees, 1.0);
            Cv2.WarpAffine(resizedSticker, rotated, rotMatrix, new Size(width, height),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            stickerToBlend = rotated;
        }

        try
        {
            using var croppedSticker = new Mat(stickerToBlend, new Rect(srcX, srcY, visible.Width, visible.Height));
            using var frameRegion = new Mat(frame, visible);

            var overlayCh = Cv2.Split(croppedSticker);
            try
            {
                using var alpha = new Mat();
                overlayCh[3].ConvertTo(alpha, MatType.CV_32FC1, 1.0 / 255.0);
                using var invAlpha = new Mat();
                Cv2.Subtract(Scalar.All(1.0), alpha, invAlpha);

                var frameCh = Cv2.Split(frameRegion);
                try
                {
                    for (var c = 0; c < 3; c++)
                    {
                        using var fFloat = new Mat();
                        using var oFloat = new Mat();
                        frameCh[c].ConvertTo(fFloat, MatType.CV_32FC1);
                        overlayCh[c].ConvertTo(oFloat, MatType.CV_32FC1);

                        using var blended = (Mat) (fFloat.Mul(invAlpha) + oFloat.Mul(alpha));
                        blended.ConvertTo(frameCh[c], MatType.CV_8UC1);
                    }
                    Cv2.Merge(frameCh, frameRegion);
                }
                finally
                {
                    foreach (var m in frameCh) m.Dispose();
                }
            }
            finally
            {
                foreach (var m in overlayCh) m.Dispose();
            }
        }
        finally
        {
            // Dispose PALING AKHIR (bukan sebelum dipakai) - croppedSticker
            // di atas cuma HEADER yang menunjuk ke buffer data "rotated" yang
            // sama (bukan salinan terpisah), jadi "rotated" harus tetap hidup
            // selama croppedSticker masih dipakai.
            rotated?.Dispose();
        }
    }

    public void Dispose() => _stickerBgra.Dispose();
}
