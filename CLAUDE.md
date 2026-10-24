# Presensi (kiosk) — Ekosistem Al-Ikhlas 86

Repo PUBLIK: jangan menulis rahasia, token, atau detail infrastruktur di sini. Konteks ekosistem lengkap ada di laptop dev pemilik: `C:\xampp\htdocs\webarsipdata-master\CLAUDE.md` dan `Projek.md`. Balas Bahasa Indonesia; commit tanpa trailer Claude.

## Isi repo
WPF kiosk wajah + barcode, target ganda **net48 (Windows 7+) dan net8.0-windows**, `LangVersion latest`. Hati-hati API yang hanya ada di net8 (mis. `ReadAsStringAsync(ct)`) — kedua target harus lolos build.
- `src/Presensi/MainWindow.xaml(.cs)` — preview kamera, alur identifikasi, lapisan hasil (centang besar hijau = tercatat, kuning = sudah tercatat, 2,5 dtk), riwayat.
- `Services/` — `CameraService` (pilih otomatis kamera yang menghasilkan gambar), `AbsenAttendanceApiClient` (kirim ke backend webview dengan header `X-KIOSK-TOKEN` dan `X-App-Version`), `UpdateService` (cek rilis saat start + tiap 6 jam), `AppVersionInfo`, `ScheduleService` (mode masuk/pulang otomatis).
- Alur wajah: frame -> backend `/api/kiosk/face-identify` -> face-service memilih wajah terdekat (banyak wajah bukan error) -> backend mencatat satu presensi per permintaan. Throttle 700 ms, cooldown 3 dtk setelah berhasil.

## Aturan
- **Jangan compile lokal.** Cek kompilasi: `gh workflow run build.yml -R al-ikhlas86/presensi` (net48 + net8 harus hijau); rilis: tag `vX.Y.Z` (asset `Presensi-net48.zip` / `Presensi-net8.zip` dicari kiosk lewat GitHub API tanpa token).
- Kiosk <=1.4.4 tidak bisa auto-update (token lama mati): timpa manual sekali. >=1.4.5 otomatis.
- Versi tampil di judul/header dan dilaporkan ke server; versi per kiosk hanya terbaca bila kiosk memakai token per perangkat (dibuat Admin IT).
- Clone `D:\Presensi` berriwayat lama: kerja lewat clone bersih, jangan push dari situ.
