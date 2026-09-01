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
        var faceBox = FaceTracker.GetFaceBox(previewFrame);
        if (faceBox is null) return; // Tidak ada wajah terdeteksi - jangan gambar apa pun drpd stiker "melayang" sembarangan.

        try
        {
            var box = faceBox.Value;
            var stickerWidth = (int) (box.Width * _manifest.WidthRatio);
            var aspectRatio = (double) _stickerBgra.Rows / _stickerBgra.Cols;
            var stickerHeight = (int) (stickerWidth * aspectRatio);
            if (stickerWidth <= 0 || stickerHeight <= 0) return;

            var centerX = box.X + box.Width / 2.0 + box.Width * _manifest.OffsetXRatio;
            var centerY = box.Y + box.Height * _manifest.AnchorYRatio;

            var destX = (int) (centerX - stickerWidth / 2.0);
            var destY = (int) (centerY - stickerHeight / 2.0);

            double rotationDegrees = 0;
#if !NET48
            // Kemiringan kepala (diminta user 2026-09-01, "kualitas kelas
            // Instagram/TikTok") - stiker ikut miring mengikuti kepala,
            // BUKAN nempel lurus terus tak peduli posisi kepala. Posisi &
            // skala di atas SENGAJA TETAP dari kotak wajah apa adanya (TIDAK
            // diubah ke basis landmark) supaya 3 stiker contoh yang sudah
            // ditata (Astronot dkk) tidak perlu ditata ulang manual -
            // landmark di sini CUMA dipakai utk sudut rotasinya.
            var landmarks = FaceTracker.GetFaceLandmarks(previewFrame);
            if (landmarks is { Length: 68 })
            {
                var rightEye = AverageOf(landmarks, 36, 37, 38, 39, 40, 41);
                var leftEye = AverageOf(landmarks, 42, 43, 44, 45, 46, 47);
                rotationDegrees = Math.Atan2(leftEye.Y - rightEye.Y, leftEye.X - rightEye.X) * 180.0 / Math.PI;
            }
#endif

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
