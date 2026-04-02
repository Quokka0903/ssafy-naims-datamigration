using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using static Simul_AcousticInfoAnalysis.Import.SdLoader; // ShortPeriodEvent, ShortPeriodSegment

namespace Simul_AcousticInfoAnalysis.ReadFileToDB
{
    public static class SpsListReader
    {
        static SpsListReader()
        {
            // EUC-KR 코드페이지 사용 가능하도록
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static List<ShortPeriodEvent> ReadSpsList(string filePath)
        {
            var events = new List<ShortPeriodEvent>();
            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[ERR ] SPSlist 파일이 없습니다: {filePath}");
                return events;
            }

            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs, Encoding.GetEncoding("euc-kr"));

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                // --- 이벤트 헤더 ---
                // C++ long  (4 bytes)
                int eventId = br.ReadInt32();
                // C++ __int64 (8 bytes)
                long startTime = br.ReadInt64();
                long endTime = br.ReadInt64();
                long eventLength = br.ReadInt64();
                // C++ UINT   (4 bytes)
                uint detectingType = br.ReadUInt32();
                // C++ long  (4 bytes)
                int segmentCount = br.ReadInt32();

                // 전시용 카운트: int (4 bytes each)
                int aiCount = br.ReadInt32();
                int trCount = br.ReadInt32();
                int unkCount = br.ReadInt32();

                // 수동/자동 플래그: UINT (4 bytes)
                uint manualAuto = br.ReadUInt32();

                // 디렉토리 경로: char[255]
                byte[] dirBuf = br.ReadBytes(255);
                if (dirBuf.Length < 255) break;
                string dirPath = Encoding.GetEncoding("euc-kr")
                                    .GetString(dirBuf)
                                    .TrimEnd('\0');

                var ev = new ShortPeriodEvent
                {
                    EventID = eventId,
                    StartTime = startTime,
                    EndTime = endTime,
                    EventLength = eventLength,
                    DetectingType = SdPackageImporter.IntToDetectType((int)detectingType),
                    SegmentCount = segmentCount,
                    AiSegmentCount = aiCount,
                    TrSegmentCount = trCount,
                    UnkSegmentCount = unkCount,
                    ManualOrAuto = manualAuto != 0,
                    DirPath = dirPath
                };

                // --- 세그먼트들 ---
                for (int j = 0; j < segmentCount; j++)
                {
                    // C++ UINT   (4 bytes)
                    uint segId = br.ReadUInt32();
                    // C++ __int64 (8 bytes)
                    long segStartTime = br.ReadInt64();
                    long segEndTime = br.ReadInt64();
                    long segLength = br.ReadInt64();
                    // C++ float  (4 bytes)
                    float ldaX = br.ReadSingle();
                    float ldaY = br.ReadSingle();
                    // C++ UINT   (4 bytes)
                    uint segType = br.ReadUInt32();
                    uint frameCount = br.ReadUInt32();
                    // C++ float  (4 bytes)
                    float startFreq = br.ReadSingle();
                    float endFreq = br.ReadSingle();
                    // C++ long   (4 bytes) → ReadInt32
                    int mod = br.ReadInt32();
                    // C++ float  (4 bytes)
                    float centerFreq = br.ReadSingle();
                    float bandwidth = br.ReadSingle();

                    var seg = new ShortPeriodSegment
                    {
                        SegmentID = (int)segId,
                        StartTime = segStartTime,
                        EndTime = segEndTime,
                        Length = segLength,
                        LDAx = ldaX,
                        LDAy = ldaY,
                        DetectingType = (int)segType,
                        FrameCount = (int)frameCount,
                        StartFreq = startFreq,
                        EndFreq = endFreq,
                        Modulation = mod,
                        CenterFreq = centerFreq,
                        BandWidth = bandwidth
                    };
                    ev.Segments.Add(seg);
                }

                events.Add(ev);
            }

            return events;
        }
    }
}
