using System.IO;
using OpenCvSharp;
using Presensi.Logging;

namespace Presensi.Filters;

/// <summary>
/// Filter berbasis file PNG (dengan channel alpha/transparansi) - dipilih
/// sebagai SATU-SATUNYA format upload user (lihat FilterManager) krn
/// gampang dibuat siapa saja pakai editor gambar apa pun (Photoshop, GIMP,
/// Canva export PNG transparan), tidak perlu tahu coding. Overlay
/// di-resize mengikuti ukuran frame kamera lalu di-alpha-blend per piksel
/// - bagian yang alpha=0 di file PNG akan tembus pandang (kelihatan wajah
/// asli), bagian alpha=255 akan menimpa penuh (mis. border/stiker/ribbon).
/// </summary>
public sealed class ImageOverlayFilter : IPreviewFilter, IDisposable
{
    public string DisplayName { get; }
    public bool IsDeletable => true;
    public string? FilePath { get; }

    private readonly Mat _overlayBgra;

    public ImageOverlayFilter(string filePath)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileNameWithoutExtension(filePath).Replace('_', ' ');

        _overlayBgra = Cv2.ImRead(filePath, ImreadModes.Unchanged);
        if (_overlayBgra.Empty())
        {
            throw new InvalidOperationException($"Gagal membaca file gambar filter: {filePath}");
        }
        if (_overlayBgra.Channels() == 3)
        {
            // Tidak ada channel alpha di file ini - dianggap solid penuh
            // (alpha=255 semua), bukan transparan.
            Cv2.CvtColor(_overlayBgra, _overlayBgra, ColorConversionCodes.BGR2BGRA);
        }
    }

    public void Apply(Mat previewFrame)
    {
        try
        {
            using var resized = new Mat();
            Cv2.Resize(_overlayBgra, resized, previewFrame.Size(), 0, 0, InterpolationFlags.Linear);

            var overlayCh = Cv2.Split(resized); // B, G, R, A
            try
            {
                using var alpha = new Mat();
                overlayCh[3].ConvertTo(alpha, MatType.CV_32FC1, 1.0 / 255.0);
                using var invAlpha = new Mat();
                Cv2.Subtract(Scalar.All(1.0), alpha, invAlpha);

                var frameCh = Cv2.Split(previewFrame); // B, G, R
                try
                {
                    for (var c = 0; c < 3; c++)
                    {
                        using var fFloat = new Mat();
                        using var oFloat = new Mat();
                        frameCh[c].ConvertTo(fFloat, MatType.CV_32FC1);
                        overlayCh[c].ConvertTo(oFloat, MatType.CV_32FC1);

                        using var blended = (Mat)(fFloat.Mul(invAlpha) + oFloat.Mul(alpha));
                        blended.ConvertTo(frameCh[c], MatType.CV_8UC1);
                    }
                    Cv2.Merge(frameCh, previewFrame);
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
        catch (Exception ex)
        {
            Log.Error("Gagal menerapkan filter gambar: " + DisplayName, ex);
        }
    }

    public void Dispose() => _overlayBgra.Dispose();
}
