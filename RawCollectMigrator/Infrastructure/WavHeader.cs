using System.IO;

namespace RawCollectMigrator.Infrastructure
{
    public readonly record struct WavHeaderInfo(int SampleRate, int Channels, int BitsPerSample, float AudioLengthSec)
    {
        public bool IsValid => SampleRate > 0 && Channels > 0 && BitsPerSample > 0;
    }

    /// <summary>
    /// 최소 RIFF/WAVE 헤더 파서. fmt/data 청크만 훑어 샘플레이트·채널·비트·길이(초)를 반환.
    /// </summary>
    public static class WavHeader
    {
        public static WavHeaderInfo TryRead(string wavPath)
        {
            try
            {
                using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != 0x46464952) return default; // "RIFF"
                br.ReadUInt32();                                    // file size
                if (br.ReadUInt32() != 0x45564157) return default; // "WAVE"

                int sampleRate = 0;
                short channels = 0, bitsPerSample = 0;
                long dataSize = 0;

                while (fs.Position + 8 <= fs.Length)
                {
                    uint chunkId = br.ReadUInt32();
                    uint chunkSize = br.ReadUInt32();

                    // RIFF 는 워드 정렬이라 청크 크기가 홀수면 뒤에 패딩 1바이트가 붙는다
                    // (패딩은 chunkSize 에 포함되지 않는다). 이를 건너뛰지 않으면 홀수 크기
                    // LIST/INFO 청크 뒤부터 모든 청크 경계가 어긋나 파싱이 실패한다.
                    long advance = chunkSize + (chunkSize & 1);

                    if (chunkId == 0x20746d66) // "fmt "
                    {
                        var pos = fs.Position;
                        br.ReadInt16();                 // audio format
                        channels = br.ReadInt16();
                        sampleRate = br.ReadInt32();
                        br.ReadInt32();                 // byte rate
                        br.ReadInt16();                 // block align
                        bitsPerSample = br.ReadInt16();
                        fs.Position = pos + advance;
                    }
                    else if (chunkId == 0x61746164) // "data"
                    {
                        dataSize = chunkSize;
                        break;
                    }
                    else
                    {
                        fs.Position += advance;
                    }
                }

                if (sampleRate == 0 || channels == 0 || bitsPerSample == 0) return default;
                int bytesPerFrame = channels * (bitsPerSample / 8);
                if (bytesPerFrame <= 0) return default;   // 8비트 미만 등 비정상 헤더 — 0 나눗셈 방지
                long totalSamples = dataSize / bytesPerFrame;
                float lengthSec = (float)totalSamples / sampleRate;
                return new WavHeaderInfo(sampleRate, channels, bitsPerSample, lengthSec);
            }
            catch
            {
                return default;
            }
        }
    }
}
