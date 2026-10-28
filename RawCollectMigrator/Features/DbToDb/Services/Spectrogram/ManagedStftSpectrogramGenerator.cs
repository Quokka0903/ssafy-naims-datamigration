using System.IO;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.DbToDb.Spectrogram
{
    // [찾기:스펙트로그램]
    /// <summary>
    /// 외부 의존 없는 관리코드 STFT 스펙트로그램 생성기.
    ///
    /// 알고리즘은 260205_server 의 IppSsSpectrogram.IPP_Spectrogram_Float 규약을 그대로 옮겼다:
    ///   · hop = frameSize / 2
    ///   · 프레임마다 Hamming 창(0.54 - 0.46·cos(2πi/(N-1)))
    ///   · 실수 FFT 후 magnitude = √(re²+im²)
    ///   · 앞 801개 빈만 저장(S0 고정), frame-major 평탄 배열
    ///
    /// 서버 원본은 Intel IPP 네이티브 DLL(ippsFFTFwd_RToCCS_32f)을 쓰지만,
    /// 폐쇄망 반입 대상에 네이티브 런타임을 늘리지 않으려고 같은 규약의 순수 C# 라딕스-2 FFT 로 구현했다.
    /// (원본은 CCS 패킹을 [2j],[2j+1] 로 일괄 해석해 DC 빈 한 칸이 어긋나는데,
    ///  여기서는 표준 해석으로 bin j = 복소빈 j 를 쓴다 — 801빈 중 첫 빈만 차이가 난다.)
    ///
    /// 실제 산출물(.dll)이 확정되면 이 클래스를 대체 구현으로 교체한다.
    /// </summary>
    public sealed class ManagedStftSpectrogramGenerator : ISpectrogramGenerator
    {
        /// <summary>S0 규약 고정 빈 수.</summary>
        public const int TargetBins = 801;

        public string Name => "Managed STFT (260205_server 규약 이식)";

        public SpectrogramResult Generate(string wavPath, int frameSize = 2048)
        {
            if (frameSize < 2 * TargetBins)
                throw new ArgumentException(
                    $"frameSize({frameSize})는 2 × {TargetBins} 이상이어야 합니다.", nameof(frameSize));
            if ((frameSize & (frameSize - 1)) != 0)
                throw new ArgumentException($"frameSize({frameSize})는 2의 거듭제곱이어야 합니다.", nameof(frameSize));

            var hdr = WavHeader.TryRead(wavPath);
            float[] audio = LoadWavMono(wavPath, out int sampleRate);
            if (sampleRate <= 0) sampleRate = hdr.IsValid ? hdr.SampleRate : 0;

            int hop = frameSize / 2;
            int frames = audio.Length >= frameSize ? ((audio.Length - frameSize) / hop) + 1 : 0;
            float nyquist = sampleRate > 0 ? sampleRate / 2f : 0f;
            // 빈 간격 = 샘플레이트 / frameSize
            float endFreq = sampleRate > 0 ? (TargetBins - 1) * (float)sampleRate / frameSize : 0f;
            if (nyquist > 0 && endFreq > nyquist) endFreq = nyquist;
            float fps = sampleRate > 0 ? (float)sampleRate / hop : 0f;

            if (frames <= 0)
                return new SpectrogramResult(Array.Empty<float>(), TargetBins, 0, 0f, endFreq, fps);

            long total = (long)frames * TargetBins;
            if (total > int.MaxValue)
                throw new InvalidDataException("스펙트로그램이 너무 큽니다(프레임 × 빈 수가 한도 초과).");

            var flat = new float[total];
            var re = new double[frameSize];
            var im = new double[frameSize];
            var win = BuildHammingWindow(frameSize);

            for (int f = 0; f < frames; f++)
            {
                int off = f * hop;
                for (int i = 0; i < frameSize; i++)
                {
                    re[i] = audio[off + i] * win[i];
                    im[i] = 0.0;
                }
                Fft(re, im);

                int dst = f * TargetBins;
                for (int j = 0; j < TargetBins; j++)
                    flat[dst + j] = (float)Math.Sqrt(re[j] * re[j] + im[j] * im[j]);
            }

            return new SpectrogramResult(flat, TargetBins, frames, 0f, endFreq, fps);
        }

        private static double[] BuildHammingWindow(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
                w[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1));
            return w;
        }

        /// <summary>제자리 라딕스-2 Cooley-Tukey FFT (길이는 2의 거듭제곱).</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;

            // 비트 반전 정렬
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr;        im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }

        /// <summary>
        /// WAV 를 모노 float 로 읽는다(멀티채널은 평균 다운믹스).
        /// PCM 8/16/24/32bit 와 IEEE float 32bit 를 처리한다 — 서버는 NAudio 를 쓰지만
        /// 폐쇄망 의존을 늘리지 않으려고 필요한 포맷만 직접 디코딩한다.
        /// RIFF 홀수 청크 패딩을 건너뛰므로 bext 등이 붙은 BWF 도 읽는다.
        /// </summary>
        private static float[] LoadWavMono(string path, out int sampleRate)
        {
            sampleRate = 0;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != 0x46464952) throw new InvalidDataException("RIFF 아님: " + Path.GetFileName(path));
            br.ReadUInt32();
            if (br.ReadUInt32() != 0x45564157) throw new InvalidDataException("WAVE 아님: " + Path.GetFileName(path));

            short channels = 0, bits = 0, format = 1;
            byte[]? data = null;

            while (fs.Position + 8 <= fs.Length)
            {
                uint id = br.ReadUInt32();
                uint size = br.ReadUInt32();
                long advance = size + (size & 1);   // RIFF 워드 정렬 패딩

                if (id == 0x20746d66)               // "fmt "
                {
                    long pos = fs.Position;
                    format = br.ReadInt16();
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32();                 // byte rate
                    br.ReadInt16();                 // block align
                    bits = br.ReadInt16();
                    fs.Position = pos + advance;
                }
                else if (id == 0x61746164)          // "data"
                {
                    long len = Math.Min(size, fs.Length - fs.Position);
                    data = br.ReadBytes((int)Math.Min(len, int.MaxValue));
                    break;
                }
                else fs.Position += advance;
            }

            if (data == null || channels <= 0 || bits <= 0)
                throw new InvalidDataException("WAV fmt/data 청크를 찾지 못함: " + Path.GetFileName(path));

            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            if (frameBytes <= 0) throw new InvalidDataException("WAV 포맷 비정상: " + Path.GetFileName(path));

            int frames = data.Length / frameBytes;
            var outBuf = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                double acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = f * frameBytes + c * bytesPerSample;
                    acc += bits switch
                    {
                        8  => (data[o] - 128) / 128.0,          // PCM 8bit 은 부호 없음(offset binary)
                        16 => BitConverter.ToInt16(data, o) / 32768.0,
                        24 => (data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16)) / 8388608.0,
                        32 => format == 3 ? BitConverter.ToSingle(data, o)
                                          : BitConverter.ToInt32(data, o) / 2147483648.0,
                        _  => throw new InvalidDataException(
                                  $"지원하지 않는 비트수 {bits}: {Path.GetFileName(path)}")
                    };
                }
                outBuf[f] = (float)(acc / channels);
            }
            return outBuf;
        }
    }
}
