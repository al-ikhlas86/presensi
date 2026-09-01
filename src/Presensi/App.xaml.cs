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

        // WAJIB diset SEBELUM menutup/membuka window apa pun - bawaan WPF
        // (OnLastWindowClose) memicu Shutdown() OTOMATIS begitu window
        // TERAKHIR tertutup, walau niatnya cuma "ganti window" (splash.Close()
        // lalu new MainWindow().Show() di baris berikutnya). TERBUKTI nyata
        // via pengujian langsung 2026-09-01: app diam-diam keluar sendiri
        // tanpa error/crash sama sekali persis di titik pindah splash ->
        // MainWindow. Dgn OnExplicitShutdown, app TIDAK PERNAH keluar sendiri
        // hanya krn 0 window sesaat - keluar sungguhan cuma lewat Shutdown()
        // eksplisit (lihat MainWindow.xaml.cs Window_Closing & early-exit
        // Mutex di atas).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        _ = RunStartupSequenceAsync();
    }

    // Popup kecil dulu (diminta user 2026-09-01) - SEBELUMNYA cek update
    // jalan di background SETELAH MainWindow (kamera) langsung tampil, jadi
    // kalau app dibuka SAAT ada update yang lagi diunduh (mis. sesi
    // sebelumnya ditutup di tengah unduhan, hangus, dicoba lagi dari nol),
    // yang kelihatan duluan langsung jendela kamera besar tanpa penjelasan.
    // Sekarang: popup kecil "Memeriksa/Memperbarui..." tampil DULU, MainWindow
    // baru dibuka SETELAH cek/unduh selesai (atau langsung kalau tidak ada
    // update - biasanya cuma sekejap, GitHub API response cepat).
    private async System.Threading.Tasks.Task RunStartupSequenceAsync()
    {
        var splash = new UpdateCheckWindow();
        splash.Show();

        void OnStartupStatus(string? msg) => Dispatcher.Invoke(() => splash.SetStatus(msg));
        UpdateService.StatusChanged += OnStartupStatus;

        // Lihat UpdateService.cs utk alasan ini ditulis sendiri (bukan lagi
        // AutoUpdater.NET.Official) & kenapa lewat GitHub API (bukan URL
        // publik biasa - repo ini privat). AppConfig.Load() dibaca ulang tiap
        // siklus (bukan cache 1x) supaya GithubToken yang baru diisi/diganti
        // manual di appsettings.json langsung kepakai tanpa restart app.
        await UpdateService.CheckAndApplyAsync(AppConfig.Load());

        // Kalau update BERHASIL diterapkan, ApplyAndRestart() DI DALAM
        // CheckAndApplyAsync SUDAH memanggil Shutdown() sendiri - baris di
        // bawah ini tidak akan sempat berefek lagi (proses sudah menutup
        // diri). Kalau tidak ada update / gagal cek, lanjut normal ke sini.
        UpdateService.StatusChanged -= OnStartupStatus;
        splash.Close();

        new MainWindow().Show();

        // Kiosk bisa jalan berhari-hari tanpa restart - cek sekali saat buka
        // SAJA tidak cukup, jadi diulang berkala. Beda dari cek awal di atas
        // (popup kecil), pengulangan ini TIDAK menutupi kamera - cukup
        // banner kecil (lihat MainWindow.xaml.cs) krn kiosk sedang dipakai.
        _updateTimer.Tick += (_, _) => _ = System.Threading.Tasks.Task.Run(() => UpdateService.CheckAndApplyAsync(AppConfig.Load()));
        _updateTimer.Start();
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
