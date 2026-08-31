using System;
using System.Windows;
using System.Windows.Threading;
using AutoUpdaterDotNET;

namespace Presensi;

public partial class App : Application
{
    // Manifest beda per target framework - PC net48 (Windows lawas) TIDAK
    // BOLEH pernah ditawari installer net8.0-windows, dan sebaliknya (beda
    // runtime, tidak saling kompatibel). Dipilih via #if compile-time
    // (bukan baca config runtime) supaya tidak mungkin salah pasang.
    // File update-net48.xml / update-net8.xml diterbitkan CI ke GitHub
    // Release tiap tag - lihat .github/workflows/build.yml.
#if NET48
    private const string UpdateManifestUrl = "https://github.com/al-ikhlas86/presensi/releases/latest/download/update-net48.xml";
#else
    private const string UpdateManifestUrl = "https://github.com/al-ikhlas86/presensi/releases/latest/download/update-net8.xml";
#endif

    // Kiosk bisa jalan berhari-hari tanpa restart (lihat komentar di bawah) -
    // cek sekali saat buka SAJA tidak cukup, jadi diulang berkala supaya
    // kiosk yang jarang dimatikan tetap kebagian update tanpa perlu restart
    // manual dari siapa pun.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };

    // Kiosk ini WAJIB jalan berhari-hari tanpa restart (dipasang di PC pos
    // satpam/TU, dinyalakan pagi lalu dibiarkan) - 1 exception tak
    // tertangani TIDAK BOLEH mematikan seluruh aplikasi begitu saja
    // (persis keluhan lama soal web absen yang harus "diakalin" biar tidak
    // perlu dipantau terus). Exception dicatat ke log, aplikasi tetap
    // hidup - lebih baik 1 fitur sempat error drpd seluruh kiosk mati dan
    // presensi anak-anak berhenti total sampai ada yang sadar & restart manual.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Tidak pakai installer sama sekali - AutoUpdater.NET.Official
        // mendukung update langsung dari file .zip (dicek API asli lib versi
        // 1.9.3 via reflection dulu, bukan tebak dari contoh lama di
        // internet): ekstrak isi zip ke InstallationPath, lalu jalankan
        // ulang ExecutablePath. Cocok utk app ini krn memang belum pernah
        // pakai installer (folder disalin manual selama ini).
        //
        // ClearAppDirectory SENGAJA false (BUKAN default contoh di dokumentasi
        // resminya yang true) - appsettings.json (KioskToken per-PC, lihat
        // AppConfig.cs) disimpan SATU FOLDER dgn Presensi.exe. Kalau folder
        // dibersihkan dulu sebelum ekstrak, token kiosk ikut hilang setiap
        // update turun dan presensi berhenti sampai ada yang input ulang
        // manual - false artinya cuma file yang ADA di zip yang ditimpa,
        // appsettings.json (tidak pernah ikut di-zip CI) tidak tersentuh.
        AutoUpdater.InstallationPath = AppContext.BaseDirectory;
        AutoUpdater.ExecutablePath = "Presensi.exe";
        AutoUpdater.ClearAppDirectory = false;
        AutoUpdater.RunUpdateAsAdmin = false;

        CheckForUpdate();
        _updateTimer.Tick += (_, _) => CheckForUpdate();
        _updateTimer.Start();
    }

    private static void CheckForUpdate()
    {
        try
        {
            AutoUpdater.Start(UpdateManifestUrl);
        }
        catch (Exception ex)
        {
            // Gagal cek update (mis. sekolah lagi tidak ada internet) TIDAK
            // BOLEH mengganggu fungsi utama kiosk (presensi wajah) - dicatat
            // lalu dilupakan, dicoba lagi otomatis di siklus berikutnya.
            Logging.Log.Error("Gagal cek update (diabaikan, aplikasi tetap jalan seperti biasa)", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.Log.Error("UI thread exception (ditahan, aplikasi tetap jalan)", e.Exception);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logging.Log.Error("Background thread exception", e.ExceptionObject as Exception);
    }
}
