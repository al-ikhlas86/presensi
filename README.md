# Presensi (Al-Ikhlas 86)

Aplikasi desktop kiosk untuk presensi wajah + scan barcode - pengganti web
Absen untuk PC pos satpam/TU, supaya tidak perlu lagi "diakalin" pakai
hotkey/CDC/CMD/Node.js buat standby di layar browser. Presensi tetap
tercatat di latar belakang meski preview kamera dimatikan atau user sedang
memakai aplikasi lain (Excel dsb).

**Status saat ini: SUDAH terhubung ke server sungguhan (Absen, lewat
Mobile-app backend) - diuji end-to-end nyata sampai ke database.** Lihat
bagian "Konfigurasi" di bawah sebelum menjalankan di PC sekolah.

## Kenapa 2 target build (net48 + net8.0-windows)?

Sekolah ini masih punya PC Windows 7. .NET modern (8+) tidak bisa jalan
sama sekali di situ, jadi 1 codebase ini di-*multi-target*:

| Target | Windows minimum | Dipakai untuk |
|---|---|---|
| `net48` | Windows 7 SP1 | PC lama |
| `net8.0-windows` | Windows 10 versi 1607+ | PC modern |

Satu solusi Visual Studio, satu `.csproj`, kode 99% sama - CI menghasilkan
2 installer terpisah tiap build (lihat `.github/workflows/build.yml`).

## Fitur

- [x] Preview kamera (bisa dimatikan tanpa menghentikan presensi di latar
      belakang - lihat `Services/CameraService.cs`)
- [x] Filter dekoratif di preview (murni tampilan, TIDAK PERNAH menyentuh
      frame yang dikirim ke pengenalan wajah - lihat `Filters/`)
- [x] Absen Masuk/Pulang **OTOMATIS** berdasar jam PC + jadwal yang diatur
      lewat tombol "Pengaturan..." (`Services/ScheduleService.cs`) - tidak
      ada lagi toggle manual, sebelum jam Pulang = Masuk, jam Pulang ke
      atas = Pulang.
- [x] Nada "berhasil" (bukan suara robot, cukup 2 nada pendek - `Services/SoundService.cs`)
- [x] Scan barcode via scanner mode CDC (serial port) - `Services/SerialBarcodeScannerService.cs`
- [x] **Presensi sungguhan** - identifikasi wajah & checkin barcode
      terhubung ke server produksi (`Services/AbsenAttendanceApiClient.cs`),
      via Mobile-app backend, yang meneruskan ke Absen. Sudah diuji nyata
      sampai tersimpan di `attendance_logs`.
- [x] Ikon aplikasi - logo resmi SDIT Al-Ikhlas / YAI 86.
- [x] **Kelola Filter** (tombol "Kelola Filter..." di sebelah dropdown
      filter) - upload/hapus filter sendiri, **2 jenis** (lihat bagian
      "Bikin Filter Sendiri" di bawah). Filter "Normal" bawaan tidak bisa
      dihapus. 3 contoh stiker (`Astronot`, `Kacamata Kumis`,
      `Telinga Kucing`) otomatis tersedia begitu pertama kali dibuka -
      boleh dihapus/diganti kapan saja. Sengaja BELUM ada tema
      Ramadan/Kemerdekaan dulu (nunggu keputusan lanjut).

## Bikin Filter Sendiri

Ada **2 jenis filter**, bedanya di cara nempelnya:

### 1. Bingkai statis (`.png`)
File PNG biasa dgn latar **transparan**, diam di 1 posisi menutupi seluruh
layar (spt bingkai foto) - TIDAK mengikuti posisi wajah. Cocok utk
border/watermark/ucapan di pinggir layar.

1. Buat kanvas baru, latar TRANSPARAN (jangan diisi warna).
2. Gambar elemen di PINGGIR kanvas, HINDARI bagian tengah (situ tempat
   wajah orang muncul).
3. Export PNG (bukan JPG - JPG tidak punya transparansi).
4. "Kelola Filter..." > "Upload Filter..." > pilih file `.png`.

### 2. Stiker nempel wajah (`.stiker`)
Ikut posisi & ukuran wajah yang terdeteksi kamera secara real-time (pakai
deteksi wajah [dlib](http://dlib.net/), lihat `Filters/FaceTracker.cs`) -
cocok utk helm/kacamata/topi/telinga dsb, spt astronot/kacamata kumis/
telinga kucing yang sudah disediakan. **Bukan cuma gambar PNG polos** -
`.stiker` sebenarnya file **ZIP yang di-rename** (contoh gampang bikinnya:
buat `.zip` biasa, baru ganti akhiran `.zip` jadi `.stiker`), isinya WAJIB
2 file:

```
nama_apa_saja.stiker  (ZIP)
├── sticker.png     <- gambar stiker, PNG transparan
└── manifest.json   <- info penempatan, lihat contoh di bawah
```

Contoh `manifest.json` (angka boleh disesuaikan coba-coba sampai pas):
```json
{
  "DisplayName": "Nama Filter yang Muncul di Dropdown",
  "WidthRatio": 1.4,
  "AnchorYRatio": -0.3,
  "OffsetXRatio": 0.0
}
```
- `WidthRatio` - lebar stiker relatif thd lebar wajah (1.0 = sama lebar).
- `AnchorYRatio` - posisi vertikal TITIK TENGAH stiker, satuan "tinggi
  wajah" dihitung dari GARIS ATAS kepala. 0 = pas di ubun-ubun, **negatif**
  = di ATAS kepala (helm/topi/telinga, mis. -0.4 s.d -0.6), **positif
  kecil** = turun ke area mata (kacamata, mis. 0.2 s.d 0.3).
- `OffsetXRatio` - geser kiri/kanan, biasanya `0` (tengah) sudah pas.

Lihat 3 contoh nyata di `Assets/SampleFilters/*.stiker` (buka pakai
7-Zip/WinRAR kalau mau intip isinya) - dibuat sendiri via script
Python+Pillow, BUKAN diunduh dari internet (filter Instagram/TikTok asli
proprietary, tidak bisa/boleh diambil dan dipakai di luar aplikasi
mereka).

**Catatan jujur soal akurasi**: ini deteksi KOTAK wajah (posisi & ukuran),
BUKAN 68-titik landmark presisi tinggi (mata/hidung persis, ikut miring
saat kepala miring) - itu butuh model tambahan ~100MB & lebih berat di
CPU, sengaja belum dipakai supaya tetap ringan di PC lawas. Kalau nanti
dirasa kurang presisi, ini bisa ditingkatkan lagi (perlu diskusi ulang
trade-off performanya).

**Kebutuhan tambahan di PC**: deteksi wajah pakai library native
([dlib](http://dlib.net/) via `DlibDotNet`) yang butuh **Visual C++
Redistributable 2017 (x64)** terpasang di PC. Kebanyakan PC Windows 10/11
sudah punya ini bawaan/dari aplikasi lain, tapi **PC Windows 7 lawas perlu
dicek manual** - kalau filter stiker error/tidak muncul di PC tertentu,
ini kemungkinan besar penyebabnya (unduh dari situs resmi Microsoft).

## Konfigurasi (WAJIB sebelum dipakai di PC sekolah)

Salin `src/Presensi/appsettings.example.json` jadi `appsettings.json` di
folder yang SAMA dengan `Presensi.exe` (bukan di source code), isi
`KioskToken` dengan token dari server (minta ke Admin IT - tersimpan di
`.env` Mobile-app backend sbg `KIOSK_API_TOKEN`). **Jangan pernah commit
`appsettings.json` yang sudah berisi token asli** (sudah di-`.gitignore`).

Kalau `appsettings.json` belum ada / `KioskToken` masih kosong, aplikasi
otomatis jalan di **mode uji** (`StubAttendanceApiClient` - selalu bilang
"berhasil" palsu, tidak benar-benar mengirim presensi) supaya tetap bisa
dicoba tanpa token dulu.

## Yang BELUM (sengaja, lihat komentar di kode)

- Belum ada installer/auto-updater (Velopack) - baru `dotnet publish` polos.
- Belum ada UI pemilihan kamera/COM-port yang proper (masih ambil device
  pertama yang ketemu).

## Build & jalankan lokal

Butuh .NET SDK 8+ terpasang (Visual Studio 2022 dengan workload ".NET
desktop development" sudah termasuk ini).

```
dotnet build src/Presensi/Presensi.csproj -f net8.0-windows
dotnet run --project src/Presensi/Presensi.csproj -f net8.0-windows
```

Ganti `net8.0-windows` jadi `net48` untuk uji target Windows lama (build
tetap bisa dari mesin Windows 10/11 manapun, tidak perlu PC Windows 7
sungguhan untuk COMPILE-nya - cuma untuk MENJALANKAN hasil buildnya).

## Build resmi (CI, hasil buat dipasang ke PC sekolah)

Compile TIDAK dilakukan di laptop manapun - 100% di server GitHub
(`windows-latest` runner), supaya laptop dev tidak lag/lama seperti build
APK Android sebelumnya. Trigger manual saja (`workflow_dispatch`), jalankan
dari tab **Actions** repo ini, atau minta AI assistant menjalankannya
(`gh workflow run build.yml`). Hasil build ada di tab **Actions** > run
yang dipilih > **Artifacts**.
