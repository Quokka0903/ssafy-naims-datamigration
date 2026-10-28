using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// R8 66B 공통 gram 헤더 파서 (신규 NAIMS2 gram 포맷).
    /// 원본 SSOT: AIATUSW/Model/GramHeader66.cs (NAIMS-II 260128_svn).
    /// 서브 프로그램은 레거시(.grm) → 신규(R8) 판별 + 헤더 요약 미리보기 용도로만 최소 이식.
    ///
    /// 바이트 레이아웃 (리틀엔디언, 66B):
    ///   GramType[2] + StartTS(16) + EndTS(16) + FrameCount(int32 4)
    ///   + IntegralTime(float 4) + BinResolution(double 8) + StartFreq(double 8) + EndFreq(double 8)
    ///
    /// TIMESTAMP(16B) = 8 × UInt16
    ///   (year, month, dayOfWeek, day, hour, minute, second, millisecond)
    ///
    /// Frame 본문: (TS16 + float[BIN]) × FrameCount  (수동 계열)
    /// 능동 계열(E0~E3)은 별도 고정 헤더 — 여기서는 미지원.
    /// </summary>
    public sealed class GramHeader66
    {
        public const int TagSize = 2;
        public const int TimestampSize = 16;
        public const int BodySize = 64;
        public const int TotalSize = 66;

        public string GramType { get; init; } = "";
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; init; }
        public int FrameCount { get; init; }
        public float IntegralTime { get; init; }
        public double BinResolution { get; init; }
        public double StartFreq { get; init; }
        public double EndFreq { get; init; }

        public bool HasValidTimeRange => StartTime > DateTime.MinValue
                                       && EndTime > DateTime.MinValue
                                       && EndTime >= StartTime;

        public double IntervalSec
        {
            get
            {
                if (FrameCount > 1 && HasValidTimeRange)
                    return (EndTime - StartTime).TotalSeconds / (FrameCount - 1);
                return IntegralTime > 0f ? IntegralTime : 0.0;
            }
        }

        public static GramHeader66? TryRead(byte[] bytes)
        {
            if (bytes == null || bytes.Length < TotalSize) return null;
            try
            {
                using var ms = new MemoryStream(bytes, 0, TotalSize, writable: false);
                using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: false);

                var tagBytes = br.ReadBytes(TagSize);
                string tag = Encoding.ASCII.GetString(tagBytes);

                var start = ReadTimestamp(br);
                var end   = ReadTimestamp(br);
                int  frameCount    = br.ReadInt32();
                float integralTime = br.ReadSingle();
                double binRes      = br.ReadDouble();
                double startFreq   = br.ReadDouble();
                double endFreq     = br.ReadDouble();

                return new GramHeader66
                {
                    GramType = tag,
                    StartTime = start,
                    EndTime = end,
                    FrameCount = frameCount,
                    IntegralTime = integralTime,
                    BinResolution = binRes,
                    StartFreq = startFreq,
                    EndFreq = endFreq,
                };
            }
            catch { return null; }
        }

        public static DateTime ReadTimestamp(BinaryReader br)
        {
            ushort y   = br.ReadUInt16();
            ushort m   = br.ReadUInt16();
            _          = br.ReadUInt16(); // dayOfWeek
            ushort d   = br.ReadUInt16();
            ushort h   = br.ReadUInt16();
            ushort min = br.ReadUInt16();
            ushort s   = br.ReadUInt16();
            ushort ms  = br.ReadUInt16();

            if (y < 1 || y > 9999 || m < 1 || m > 12 || h > 23 || min > 59 || s > 59 || ms > 999)
                return DateTime.MinValue;

            int daysInMonth;
            try { daysInMonth = DateTime.DaysInMonth(y, m); }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
            if (d < 1 || d > daysInMonth) return DateTime.MinValue;

            return new DateTime(y, m, d, h, min, s, ms, DateTimeKind.Utc);
        }

        /// <summary>알려진 R8 gram 태그 세트. 매치되지 않으면 레거시/미상.</summary>
        public bool IsKnownR8Tag => GramType switch
        {
            "L0" or "D0" or "V1" or "V2" or "V3" or "V4" or
            "DI" or "B0" or "S0" or "A0" or
            "E0" or "E1" or "E2" or "E3" => true,
            _ => false
        };
    }
}
