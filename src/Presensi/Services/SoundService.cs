using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Nada tanda "berhasil" - SENGAJA bukan suara robot bicara (permintaan
/// eksplisit user), cukup 2 nada pendek (mirip "ding-dong" bel) yang
/// di-generate langsung di memori (bukan file .wav eksternal) - supaya
/// tidak perlu aset audio pihak ketiga sama sekali & repo tetap ringan.
/// Diputar lewat perangkat output audio DEFAULT Windows - kalau nanti
/// perlu pilih speaker tertentu (bukan default), tambahkan NAudio
/// (WasapiOut) di titik ini, sengaja belum dipasang sekarang biar
/// dependency tetap minim.
/// </summary>
public sealed class SoundService
{
    private readonly byte[] _successChimeWav;

    public SoundService()
    {
        _successChimeWav = GenerateChimeWav();
    }

    public void PlaySuccess()
    {
        try
        {
            using var stream = new MemoryStream(_successChimeWav);
            using var player = new SoundPlayer(stream);
            player.Play(); // async, tidak memblokir UI/capture loop
        }
        catch (Exception ex)
        {
            Log.Error("Gagal memutar nada berhasil", ex);
        }
    }

    private static byte[] GenerateChimeWav()
    {
        const int sampleRate = 44100;
        // 2 nada naik (mirip bel pintu) - masing2 120ms, total 240ms.
        var tones = new (double freqHz, int durationMs)[] { (880, 120), (1318.5, 120) };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var samples = new List<short>();
        foreach (var (freq, durationMs) in tones)
        {
            int sampleCount = sampleRate * durationMs / 1000;
            for (int i = 0; i < sampleCount; i++)
            {
                // Fade-out linear tiap nada supaya tidak "klik" kasar di ujung.
                double fade = 1.0 - (double)i / sampleCount;
                double t = i / (double)sampleRate;
                double value = Math.Sin(2 * Math.PI * freq * t) * fade * 0.5;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        int dataLength = samples.Count * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // byte rate (16-bit mono)
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (var s in samples) writer.Write(s);

        return ms.ToArray();
    }
}
