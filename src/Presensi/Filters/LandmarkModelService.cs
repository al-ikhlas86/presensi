#if !NET48
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DlibDotNet;
using Presensi.Logging;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace Presensi.Filters;

/// <summary>
/// Model deteksi 68 titik wajah (mata, hidung, mulut, garis rahang) - dipakai
/// FaceStickerFilter utk penempatan/rotasi stiker presisi ala Instagram/
/// TikTok (diminta user 2026-09-01), KHUSUS net8.0-windows (PC Windows
/// 10/11). net48/Windows 7 TIDAK dapat fitur ini sama sekali (tetap pakai
/// deteksi kotak-wajah sederhana yang sudah ada di FaceTracker) - PC lawas
/// & bukan yang diminta user utk peningkatan ini.
///
/// SENGAJA TIDAK dibundel ke hasil publish/zip auto-update (~95MB kalau
/// dibundel akan MEMBESARKAN SETIAP unduhan update ke depannya, padahal
/// modelnya sendiri cuma perlu ada SEKALI per-PC) - diunduh sendiri saat
/// pertama kali aplikasi dibuka, disimpan permanen di %LocalAppData%\
/// Presensi\models\, dipakai lagi selamanya sesudah itu (termasuk lintas
/// update app - lihat UpdateService.cs, folder ini bukan bagian dari
/// instalasi yang ditimpa).
///
/// Sumber file: mirror resmi davisking/dlib-models di GitHub (pembuat asli
/// library dlib) - DIVERIFIKASI LANGSUNG sebelum kode ini ditulis (diunduh,
/// didekompresi via SharpCompress, dimuat lewat ShapePredictor.Deserialize()
/// - berhasil, 68 landmark terbaca benar), bukan asumsi URL/format.
/// </summary>
public static class LandmarkModelService
{
    private const string ModelUrl = "https://raw.githubusercontent.com/davisking/dlib-models/master/shape_predictor_68_face_landmarks.dat.bz2";
    private static readonly string ModelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Presensi", "models");
    private static readonly string ModelPath = Path.Combine(ModelDir, "shape_predictor_68_face_landmarks.dat");

    private static readonly object Gate = new();
    private static Task? _loadingTask;

    /// <summary>Null selama belum siap (belum dipanggil EnsureLoadedAsync,
    /// masih proses unduh, atau gagal unduh/muat) - pemanggil (FaceTracker)
    /// WAJIB fallback ke mode kotak biasa saat null, TIDAK BOLEH menunggu/
    /// blocking di sini sama sekali (jalur ini dipanggil dari loop preview
    /// kamera).</summary>
    public static ShapePredictor? Predictor { get; private set; }

    /// <summary>Mulai proses siapkan model di background kalau belum pernah
    /// dipanggil - aman dipanggil berkali-kali (idempotent, cukup 1 task
    /// yang benar-benar jalan).</summary>
    public static Task EnsureLoadedAsync()
    {
        lock (Gate)
        {
            _loadingTask ??= Task.Run(LoadOrDownloadAsync);
            return _loadingTask;
        }
    }

    private static async Task LoadOrDownloadAsync()
    {
        try
        {
            if (!File.Exists(ModelPath))
            {
                Directory.CreateDirectory(ModelDir);
                Log.Info("[Landmark] Model belum ada, mengunduh (~64MB terkompresi, sekali saja per-PC)...");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Presensi-AlIkhlas86");
                var bz2Bytes = await http.GetByteArrayAsync(ModelUrl).ConfigureAwait(false);

                // Ekstrak ke file .tmp dulu, baru rename ke nama final - kalau
                // proses mati di tengah unduhan/ekstraksi (mis. PC dimatikan),
                // file .dat final TIDAK PERNAH setengah jadi (File.Exists di
                // atas akan tetap false, dicoba ulang bersih dari awal).
                var tempBz2 = ModelPath + ".bz2.tmp";
                var tempDat = ModelPath + ".tmp";
                await File.WriteAllBytesAsync(tempBz2, bz2Bytes).ConfigureAwait(false);
                using (var input = File.OpenRead(tempBz2))
                using (var bz2 = new BZip2Stream(input, CompressionMode.Decompress, false))
                using (var output = File.Create(tempDat))
                {
                    await bz2.CopyToAsync(output).ConfigureAwait(false);
                }
                File.Delete(tempBz2);
                File.Move(tempDat, ModelPath);
                Log.Info("[Landmark] Model selesai diunduh & disiapkan: " + ModelPath);
            }

            Predictor = ShapePredictor.Deserialize(ModelPath);
            Log.Info($"[Landmark] Model siap dipakai ({Predictor.Parts} titik).");
        }
        catch (Exception ex)
        {
            // Gagal unduh/muat (mis. tidak ada internet saat pertama kali
            // dipakai) TIDAK BOLEH mengganggu presensi sama sekali - filter
            // cukup fallback ke mode kotak biasa (Predictor tetap null),
            // dicoba lagi otomatis saat app dibuka ulang berikutnya.
            Log.Error("[Landmark] Gagal menyiapkan model - filter presisi dilewati, pakai mode kotak biasa", ex);
        }
    }
}
#endif
