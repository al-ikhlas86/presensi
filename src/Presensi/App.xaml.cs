using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Presensi.Services;

namespace Presensi;

public partial class App : Application
{
    // Kiosk bisa jalan berhari-hari tanpa restart (lihat komentar di bawah) -
    // cek sekali saat buka SAJA tidak cukup, jadi diulang berkala supaya
    // kiosk yang jarang dimatikan tetap kebagian update tanpa perlu restart
    // manual dari siapa pun.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };

    // Mutex bernama - cegah 2 proses Presensi.exe jalan BERSAMAAN. Terbukti
    // nyata terjadi 2026-09-01 (log: 2x "Kamera index 0 dimulai" persis di
    // detik yang sama, keduanya rebutan kamera & sama2 mencoba presensi) -
    // bisa dipicu auto-update relaunch yang bertepatan dgn proses lama yang
    // belum benar2 keluar, atau user membuka app 2x tanpa sadar. Mutex
    // dilepas otomatis oleh OS begitu proses berakhir (termasuk lewat
    // Application.Shutdown() saat auto-update, lihat UpdateService.cs) -
    // jadi proses baru hasil relaunch update TIDAK akan pernah nyangkut
    // menganggap dirinya "duplikat" dari proses lama yang memang sedang
    // menutup diri secara sah.
    private static Mutex? _singleInstanceMutex;

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

        _singleInstanceMutex = new Mutex(true, "Presensi_AlIkhlas86_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // MainWindow BELUM dibuat sama sekali di titik ini (StartupUri
            // sudah dihapus dari App.xaml, lihat catatan di sana) - jadi
            // instance kedua yang gagal dapat Mutex benar2 tidak pernah
            // menyalakan kamera/apa pun, murni langsung keluar bersih.
            // TERBUKTI beda dari pendekatan StartupUri lama via pengujian
            // langsung 2026-09-01 (instance kedua sempat sekilas menyalakan
            // kamera sebelum akhirnya tertutup).
            Logging.Log.Warn("Sudah ada Presensi.exe lain yang jalan - instance ini ditutup otomatis.");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Lihat UpdateService.cs utk alasan ini ditulis sendiri (bukan lagi
        // AutoUpdater.NET.Official) & kenapa lewat GitHub API (bukan URL
        // publik biasa - repo ini privat). AppConfig.Load() dibaca ulang tiap
        // siklus (bukan cache 1x) supaya GithubToken yang baru diisi/diganti
        // manual di appsettings.json langsung kepakai tanpa restart app.
        // Dijalankan di background thread (Task.Run), BUKAN langsung di UI
        // thread, krn ada unduhan file besar (~100MB+) yang tidak boleh
        // membekukan tampilan kiosk selama proses cek/unduh berlangsung.
        _ = System.Threading.Tasks.Task.Run(() => UpdateService.CheckAndApplyAsync(AppConfig.Load()));
        _updateTimer.Tick += (_, _) => _ = System.Threading.Tasks.Task.Run(() => UpdateService.CheckAndApplyAsync(AppConfig.Load()));
        _updateTimer.Start();

        // StartupUri dihapus dari App.xaml - window dibuat manual DI SINI,
        // baru SETELAH lolos cek Mutex di atas.
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* proses memang lagi keluar, abaikan */ }
        base.OnExit(e);
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
