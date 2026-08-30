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

    public static void EnsureSeeded()
    {
        try
        {
            Directory.CreateDirectory(FiltersDir);
            if (Directory.GetFiles(FiltersDir, "*.png").Length > 0) return;

            // Contoh filter bawaan (originaL, dibuat sendiri - lihat
            // README/Projek.md soal kenapa bukan diunduh dari internet)
            // disalin sekali di first-run supaya user langsung punya
            // sesuatu utk dicoba tanpa perlu upload dulu.
            var sampleDir = Path.Combine(AppContext.BaseDirectory, "SampleFilters");
            if (!Directory.Exists(sampleDir)) return;

            foreach (var file in Directory.GetFiles(sampleDir, "*.png"))
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
            foreach (var file in Directory.GetFiles(FiltersDir, "*.png").OrderBy(f => f))
            {
                try
                {
                    list.Add(new ImageOverlayFilter(file));
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

    /// <summary>Salin file PNG yang dipilih user ke folder filter. Melempar exception dgn pesan Indonesia kalau ekstensi tidak didukung.</summary>
    public static string Import(string sourceFilePath)
    {
        if (!string.Equals(Path.GetExtension(sourceFilePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Hanya file .png yang didukung (idealnya dgn latar transparan).");
        }

        Directory.CreateDirectory(FiltersDir);
        var destName = Path.GetFileName(sourceFilePath);
        var dest = Path.Combine(FiltersDir, destName);
        var i = 1;
        while (File.Exists(dest))
        {
            dest = Path.Combine(FiltersDir, Path.GetFileNameWithoutExtension(destName) + $"_{i}.png");
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
