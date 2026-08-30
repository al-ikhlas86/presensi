using System.IO;
using System.Linq;
using Presensi.Logging;

namespace Presensi.Filters;

/// <summary>
/// Kelola filter berbasis file PNG di %LocalAppData%\Presensi\filters\ -
/// "Normal" (NoFilter) SELALU ada & tidak bisa dihapus (dijamin di kode,
/// bukan cuma UI), sisanya murni file PNG yang user upload sendiri lewat
/// FilterManagerWindow. Beda PC = beda isi folder ini (tidak disinkron ke
/// server, murni lokal per-kiosk).
/// </summary>
public static class FilterManager
{
    public static string FiltersDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Presensi", "filters");

    private static readonly string[] SupportedExtensions = { ".png", ".stiker" };

    public static void EnsureSeeded()
    {
        try
        {
            Directory.CreateDirectory(FiltersDir);
            if (Directory.EnumerateFiles(FiltersDir).Any(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))) return;

            // Contoh filter bawaan (original, dibuat sendiri - lihat
            // README/Projek.md soal kenapa bukan diunduh dari internet)
            // disalin sekali di first-run supaya user langsung punya
            // sesuatu utk dicoba tanpa perlu upload dulu.
            var sampleDir = Path.Combine(AppContext.BaseDirectory, "SampleFilters");
            if (!Directory.Exists(sampleDir)) return;

            foreach (var file in Directory.EnumerateFiles(sampleDir).Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                var dest = Path.Combine(FiltersDir, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Gagal menyiapkan folder filter", ex);
        }
    }

    public static List<IPreviewFilter> LoadAll()
    {
        var list = new List<IPreviewFilter> { new NoFilter() };
        try
        {
            Directory.CreateDirectory(FiltersDir);
            var files = Directory.EnumerateFiles(FiltersDir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f);

            foreach (var file in files)
            {
                try
                {
                    IPreviewFilter filter = Path.GetExtension(file).ToLowerInvariant() switch
                    {
                        ".stiker" => new FaceStickerFilter(file),
                        _ => new ImageOverlayFilter(file),
                    };
                    list.Add(filter);
                }
                catch (Exception ex)
                {
                    Log.Error("Melewati file filter rusak: " + file, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Gagal memuat daftar filter", ex);
        }
        return list;
    }

    /// <summary>
    /// Salin file filter yang dipilih user ke folder filter - ".png" (bingkai
    /// statis full-frame) atau ".stiker" (nempel-di-wajah, lihat
    /// FaceStickerFilter). Melempar exception dgn pesan Indonesia kalau
    /// ekstensi tidak didukung.
    /// </summary>
    public static string Import(string sourceFilePath)
    {
        var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("Cuma file .png (bingkai statis) atau .stiker (nempel di wajah) yang didukung.");
        }

        Directory.CreateDirectory(FiltersDir);
        var destName = Path.GetFileName(sourceFilePath);
        var dest = Path.Combine(FiltersDir, destName);
        var i = 1;
        while (File.Exists(dest))
        {
            dest = Path.Combine(FiltersDir, Path.GetFileNameWithoutExtension(destName) + $"_{i}" + ext);
            i++;
        }
        File.Copy(sourceFilePath, dest);
        return dest;
    }

    public static void Delete(IPreviewFilter filter)
    {
        if (!filter.IsDeletable || filter.FilePath is null)
        {
            throw new InvalidOperationException("Filter \"" + filter.DisplayName + "\" tidak bisa dihapus.");
        }
        if (File.Exists(filter.FilePath)) File.Delete(filter.FilePath);
    }
}
