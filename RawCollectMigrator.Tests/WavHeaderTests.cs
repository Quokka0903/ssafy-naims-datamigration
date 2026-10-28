using System;
using System.IO;
using System.Text;
using RawCollectMigrator.Infrastructure;
using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// WavHeader(최소 RIFF/WAVE 파서) 단위 테스트.
    ///
    /// 인메모리로 WAV 바이트를 조립해 임시 파일로 검증한다.
    /// ※ RIFF 규격: 청크 크기가 홀수면 청크 뒤에 패딩 1바이트가 붙는다(청크는 워드 정렬).
    ///   파서는 다음 청크를 읽기 전에 이 패딩을 건너뛰어야 한다 — 홀수 LIST 청크 테스트가 그 계약이다.
    /// </summary>
    public sealed class WavHeaderTests
    {
        // ── WAV 바이트 조립 도우미 ───────────────────────────────────────────

        private static byte[] BuildWav(Action<BinaryWriter> writeChunks)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(0);                                    // 파일 크기 자리표시 — 아래에서 패치
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            writeChunks(bw);
            bw.Flush();
            var bytes = ms.ToArray();
            BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);   // RIFF 크기 = 전체 - 8
            return bytes;
        }

        private static void WriteFmt(BinaryWriter bw, short channels, int sampleRate, short bits)
        {
            int blockAlign = channels * (bits / 8);
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                       // PCM fmt 청크 크기
            bw.Write((short)1);                 // PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * blockAlign);  // byte rate
            bw.Write((short)blockAlign);
            bw.Write(bits);
        }

        private static void WriteData(BinaryWriter bw, int dataSize)
        {
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            bw.Write(new byte[dataSize]);       // 무음 페이로드
        }

        /// <summary>size 바이트 페이로드의 LIST 청크. RIFF 규격대로 홀수 크기면 패딩 1바이트를 붙인다.</summary>
        private static void WriteListChunk(BinaryWriter bw, int size)
        {
            bw.Write(Encoding.ASCII.GetBytes("LIST"));
            bw.Write(size);
            bw.Write(new byte[size]);
            if (size % 2 != 0) bw.Write((byte)0);   // 워드 정렬 패딩 — 청크 크기에는 포함되지 않는다
        }

        /// <summary>바이트를 임시 .wav 로 쓰고 TryRead 한 뒤 파일을 지운다.</summary>
        private static WavHeaderInfo ReadWav(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "wavtest_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(path, bytes);
                return WavHeader.TryRead(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* 임시 파일 정리 실패는 무시 */ }
            }
        }

        // ── 정상 파싱 ────────────────────────────────────────────────────────

        [Theory]
        [InlineData(8000, (short)1, (short)16, 16000, 1.0f)]     // 8k 모노 16bit, 1초
        [InlineData(44100, (short)2, (short)16, 44100, 0.25f)]   // 44.1k 스테레오 16bit, 0.25초
        [InlineData(16000, (short)4, (short)8, 32000, 0.5f)]     // 16k 4채널 8bit, 0.5초
        public void TryRead_StandardPcm_ParsesRateChannelsBitsAndLength(
            int sampleRate, short channels, short bits, int dataSize, float expectedSec)
        {
            var bytes = BuildWav(bw =>
            {
                WriteFmt(bw, channels, sampleRate, bits);
                WriteData(bw, dataSize);
            });

            var info = ReadWav(bytes);

            Assert.True(info.IsValid);
            Assert.Equal(sampleRate, info.SampleRate);
            Assert.Equal(channels, info.Channels);
            Assert.Equal(bits, info.BitsPerSample);
            Assert.Equal(expectedSec, info.AudioLengthSec, 3);
        }

        [Fact]
        public void TryRead_EvenSizedListChunkBeforeFmt_IsSkipped()
        {
            var bytes = BuildWav(bw =>
            {
                WriteListChunk(bw, 26);                 // 짝수 크기 — 패딩 없음
                WriteFmt(bw, 1, 8000, 16);
                WriteData(bw, 16000);
            });

            var info = ReadWav(bytes);

            Assert.True(info.IsValid);
            Assert.Equal(8000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.Equal(16, info.BitsPerSample);
        }

        [Fact]
        public void TryRead_OddSizedListChunkBeforeFmt_SkipsPadByte_PerRiffSpec()
        {
            // RIFF 규격: 홀수 크기 청크 뒤에는 정렬용 패딩 1바이트가 붙고, 그 패딩은 청크 크기에 포함되지 않는다.
            // 파서가 패딩을 건너뛰지 않으면 다음 청크 ID 가 1바이트 밀려 fmt 를 놓친다.
            var bytes = BuildWav(bw =>
            {
                WriteListChunk(bw, 5);                  // 홀수 크기 + 패딩 1바이트
                WriteFmt(bw, 2, 44100, 16);
                WriteData(bw, 44100);
            });

            var info = ReadWav(bytes);

            Assert.True(info.IsValid, "홀수 청크 패딩 스킵 실패 — RIFF 워드 정렬 미준수");
            Assert.Equal(44100, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.Equal(16, info.BitsPerSample);
            Assert.Equal(0.25f, info.AudioLengthSec, 3);
        }

        [Fact]
        public void TryRead_FmtWithoutData_ValidFormatButZeroLength()
        {
            // data 청크가 없으면 포맷 정보만 유효하고 길이는 0초.
            var bytes = BuildWav(bw => WriteFmt(bw, 1, 8000, 16));

            var info = ReadWav(bytes);

            Assert.True(info.IsValid);
            Assert.Equal(8000, info.SampleRate);
            Assert.Equal(0f, info.AudioLengthSec, 3);
        }

        // ── 손상 파일 계약: 예외 없이 default(IsValid=false) ─────────────────

        [Fact]
        public void TryRead_NotRiff_ReturnsInvalid()
        {
            var bytes = Encoding.ASCII.GetBytes("JUNKJUNKJUNKJUNKJUNKJUNK");
            var info = ReadWav(bytes);
            Assert.False(info.IsValid);
            Assert.Equal(default, info);
        }

        [Fact]
        public void TryRead_RiffButNotWave_ReturnsInvalid()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(4);
            bw.Write(Encoding.ASCII.GetBytes("AVI "));
            bw.Flush();

            var info = ReadWav(ms.ToArray());
            Assert.False(info.IsValid);
        }

        [Fact]
        public void TryRead_NoFmtChunk_ReturnsInvalid()
        {
            var bytes = BuildWav(bw => WriteData(bw, 100));   // fmt 없이 data 만
            var info = ReadWav(bytes);
            Assert.False(info.IsValid);
        }

        [Fact]
        public void TryRead_TruncatedMidHeader_ReturnsInvalid()
        {
            var full = BuildWav(bw =>
            {
                WriteFmt(bw, 1, 8000, 16);
                WriteData(bw, 16000);
            });
            var truncated = new byte[10];                     // "RIFF"+크기+WAVE 도중에 잘림
            Array.Copy(full, truncated, truncated.Length);

            var info = ReadWav(truncated);
            Assert.False(info.IsValid);
        }

        [Fact]
        public void TryRead_ZeroChannels_ReturnsInvalid_NoDivideByZero()
        {
            var bytes = BuildWav(bw =>
            {
                WriteFmt(bw, 0, 8000, 16);                    // channels=0 — 0나눗셈 방어 계약
                WriteData(bw, 16000);
            });

            var info = ReadWav(bytes);
            Assert.False(info.IsValid);
        }

        [Fact]
        public void TryRead_MissingFile_ReturnsInvalid()
        {
            string path = Path.Combine(Path.GetTempPath(), "wavtest_missing_" + Guid.NewGuid().ToString("N") + ".wav");
            var info = WavHeader.TryRead(path);
            Assert.False(info.IsValid);
            Assert.Equal(default, info);
        }

        // ══════════ [찾기:NDID] 원음이 적어 둔 구간 시각 ══════════
        /// <summary>
        /// NDID(레거시 FACT) 배치는 정본 <c>260128_svn/AIATUSW/Worker/WavFileWorker.cs</c> 의
        /// <c>FactChunk</c> 그대로다: @0 StartTime · @4 StartUsec · @8 EndTime · @12 EndUsec · @48 Comment[81].
        /// 오프셋이 밀리면 <b>엉뚱한 숫자를 시각으로 읽는다</b> — 그래도 그림은 그려지므로 아무도 못 알아챈다.
        /// </summary>
        private static byte[] MakeBwf(int startSec, int endSec, string comment, string chunkId = "NDID")
        {
            var ndid = new byte[512];
            BitConverter.GetBytes(startSec).CopyTo(ndid, 0);
            BitConverter.GetBytes(endSec).CopyTo(ndid, 8);
            System.Text.Encoding.ASCII.GetBytes(comment).CopyTo(ndid, 48);

            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(0);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("bext"));   // 실제 파일처럼 앞에 bext 를 둔다
            bw.Write(858); bw.Write(new byte[858]);
            bw.Write(System.Text.Encoding.ASCII.GetBytes(chunkId));
            bw.Write(512); bw.Write(ndid);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(4); bw.Write(new byte[4]);
            bw.Flush();
            return ms.ToArray();
        }

        [Fact]
        public void NDID_자정이후초를_시각으로_읽는다()
        {
            // 실측 nb_37577863: @0=36896, @8=36936 → 10:14:56, 길이 40초(원음과 일치)
            var t = NdidChunk.TryRead(MakeBwf(36896, 36936, "TEST"));

            Assert.NotNull(t);
            Assert.Equal(36896, t!.StartSecOfDay);
            Assert.Equal(36936, t.EndSecOfDay);
            Assert.Equal(40, t.EndSecOfDay - t.StartSecOfDay);
            Assert.Equal("TEST", t.Comment);

            // 하루치보다 작으면 <날짜가 없는 것>이다 — 1970 을 지어내지 않는다.
            Assert.Null(t.AbsoluteStart);
        }

        [Fact]
        public void NDID_진짜_Unix시각이면_절대시각으로_읽는다()
        {
            // 2017-09-18 10:26:46 UTC
            int unix = (int)new DateTimeOffset(2017, 9, 18, 10, 26, 46, TimeSpan.Zero).ToUnixTimeSeconds();
            var t = NdidChunk.TryRead(MakeBwf(unix, unix + 40, ""));

            Assert.NotNull(t);
            Assert.NotNull(t!.AbsoluteStart);
            Assert.Equal(new DateTime(2017, 9, 18, 10, 26, 46), t.AbsoluteStart!.Value);
            Assert.Equal(10 * 3600 + 26 * 60 + 46, t.StartSecOfDay);
        }

        [Fact]
        public void NDID_없으면_null()
        {
            var noNdid = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(noNdid);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(0);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(4); bw.Write(new byte[4]);
            bw.Flush();
            Assert.Null(NdidChunk.TryRead(noNdid.ToArray()));
        }
    }
}
