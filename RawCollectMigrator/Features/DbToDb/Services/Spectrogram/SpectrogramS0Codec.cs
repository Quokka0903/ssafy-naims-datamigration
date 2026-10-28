using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.DbToDb.Spectrogram
{
    // [찾기:스펙트로그램] [찾기:S0코덱]
    /// <summary>
    /// 스펙트로그램 blob 의 S0(R8 66B) 직렬화기.
    ///
    /// 대상 컬럼:
    ///   · STD_TR_SNIPPET.SPECTROGRAM_DATA
    ///   · STD_AI_SPECTROGRAM.GRAM_DATA
    ///
    /// 규격은 AIATUSW(260128_svn) 의 Model/STFT/SpectrogramS0Codec.Serialize 및
    /// Model/GramReader.ReadStftGram 과 바이트 단위로 동일해야 한다. 전시 측이 그대로 파싱한다:
    ///
    ///   "S0" (2B ASCII)
    ///   + GramHeader66 본문(64B)
    ///       StartTS16(16B) + EndTS16(16B)
    ///       + FrameCount(int32) + IntegralTime(float)
    ///       + BinResolution(double) + StartFreq(double) + EndFreq(double)
    ///   + 프레임마다 [ TS16(16B) + Amplitude float[BinCount] ]   ← frame-major
    ///
    /// TS16 은 ushort 8개: Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond (UTC).
    /// 프레임 타임스탬프는 원본과 같이 시작~끝을 균등 보간해 기록한다
    /// (StftGram 이 프레임별 시각을 따로 갖지 않기 때문).
    ///
    /// ⚠ 순수 float 배열을 넣으면 안 된다 — 전시 측 Handle_S0 가 선두 2바이트를 "S0" 로 검사한다.
    /// </summary>
    public static class SpectrogramS0Codec
    {
        /// <summary>"S0" 2B + 본문 64B.</summary>
        public const int HeaderBytes = 66;

        /// <summary>
        /// 스펙트로그램을 S0 blob 으로 직렬화한다. 빈 결과면 빈 배열을 돌려준다(예외 아님).
        /// </summary>
        /// <param name="sp">frame-major 진폭을 담은 산출 결과.</param>
        /// <param name="startUtc">그램 시작 시각(UTC).</param>
        /// <param name="endUtc">그램 종료 시각(UTC).</param>
        /// <param name="integralTime">적분 시간(초). 모르면 0.</param>
        public static byte[] Serialize(SpectrogramResult sp, DateTime startUtc, DateTime endUtc, float integralTime = 0f)
        {
            if (sp is null || sp.IsEmpty || sp.BinCount <= 0) return Array.Empty<byte>();

            startUtc = EnsureUtc(startUtc);
            endUtc = EnsureUtc(endUtc);

            long stepTicks = 0;
            if (sp.FrameCount > 1 && startUtc != default && endUtc != default && endUtc > startUtc)
                stepTicks = (endUtc.Ticks - startUtc.Ticks) / (sp.FrameCount - 1);

            // 빈 간격(주파수 해상도). 원본은 double 로 기록한다.
            double binRes = sp.BinCount > 1 ? (sp.EndFreq - sp.StartFreq) / (sp.BinCount - 1) : 0.0;

            using var ms = new MemoryStream(HeaderBytes + sp.FrameCount * (16 + sp.BinCount * sizeof(float)));
            using var bw = new BinaryWriter(ms, Encoding.ASCII);

            bw.Write((byte)'S');
            bw.Write((byte)'0');
            WriteTimestamp16(bw, startUtc);
            WriteTimestamp16(bw, endUtc);
            bw.Write(sp.FrameCount);
            bw.Write(integralTime);
            bw.Write(binRes);
            bw.Write((double)sp.StartFreq);
            bw.Write((double)sp.EndFreq);

            for (int f = 0; f < sp.FrameCount; f++)
            {
                var ts = stepTicks > 0 && startUtc != default
                    ? startUtc.AddTicks(stepTicks * f)
                    : startUtc;
                WriteTimestamp16(bw, ts);

                int off = f * sp.BinCount;
                for (int b = 0; b < sp.BinCount; b++)
                    bw.Write(sp.Amplitude[off + b]);
            }

            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>TS16: ushort 8개(Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond).</summary>
        private static void WriteTimestamp16(BinaryWriter bw, DateTime dtUtc)
        {
            dtUtc = EnsureUtc(dtUtc);
            bw.Write((ushort)dtUtc.Year);
            bw.Write((ushort)dtUtc.Month);
            bw.Write((ushort)dtUtc.DayOfWeek);
            bw.Write((ushort)dtUtc.Day);
            bw.Write((ushort)dtUtc.Hour);
            bw.Write((ushort)dtUtc.Minute);
            bw.Write((ushort)dtUtc.Second);
            bw.Write((ushort)dtUtc.Millisecond);
        }

        private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
    }
}
