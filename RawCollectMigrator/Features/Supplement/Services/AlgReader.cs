using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.Supplement
{
    /// <summary>
    /// 보조정보.alg 파서 (전체 구조).
    /// Main 프로젝트 Features/Supplement/AlgReader.cs 이식 — 서브프로그램용.
    /// CP949(EUC-KR) 고정폭 필드를 순차 읽고 세트 배열을 파싱.
    /// </summary>
    public static class AlgReader
    {
        static AlgReader()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static SupplementSheet? TryRead(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try { return Read(filePath); }
            catch { return null; }
        }

        public static SupplementSheet Read(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            enc ??= Encoding.GetEncoding(949);

            using var sr = new StreamReader(filePath, enc, detectEncodingFromByteOrderMarks: true);
            var stream = sr.BaseStream;

            byte[] NextBytes(int length)
            {
                var buffer = new byte[length];
                int read = stream.Read(buffer, 0, length);
                if (read <= 0) throw new EndOfStreamException("Unexpected EOF while reading .alg");
                if (read < length) Array.Resize(ref buffer, read);
                return buffer;
            }

            string Decode(byte[] buffer) => enc.GetString(buffer).Trim();
            int NextInt()
            {
                var v = Decode(NextBytes(8));
                return int.TryParse(v, out var r) ? r : 0;
            }
            double NextDouble()
            {
                var v = Decode(NextBytes(16));
                return double.TryParse(v, out var r) ? r : 0.0;
            }

            var sheet = new SupplementSheet();

            // MissionInfo
            sheet.MissionInfo = new SupplementMissionInfo
            {
                DetPlatform    = Decode(NextBytes(30)),
                DetVesselClass = Decode(NextBytes(20)),
                DetVesselName  = Decode(NextBytes(20)),
                MissionDateFr  = Decode(NextBytes(8)),
                MissionDateTo  = Decode(NextBytes(8)),
                MissionSeaArea = Decode(NextBytes(20)),
                MissionSummary = Decode(NextBytes(80)),
                MissionComment = Decode(NextBytes(80)),
            };
            NextBytes(2); // 개행

            // Recmedia
            int recmediaCount = NextInt();
            NextBytes(2);
            for (int i = 0; i < recmediaCount; i++)
            {
                var rm = new SupplementRecmedia { Index = i + 1, RecordCount = NextInt() };
                NextBytes(2);
                for (int j = 0; j < rm.RecordCount; j++)
                {
                    rm.Records.Add(new SupplementRecord
                    {
                        RecmediaIndex = i + 1,
                        Index = j + 1,
                        RecordTimeFr = Decode(NextBytes(14)),
                        RecordTimeTo = Decode(NextBytes(14)),
                        RecordComment = Decode(NextBytes(80)),
                    });
                    NextBytes(2);
                }
                sheet.Recmedia.Add(rm);
            }

            // SetCount
            sheet.SetCount = NextInt();
            NextBytes(2);

            // NaviSeaInfo × SetCount
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var n = new SupplementNaviSeaInfo
                {
                    SetIndex   = i + 1,
                    DetDate    = Decode(NextBytes(8)),
                    DetTime    = Decode(NextBytes(8)),
                    Depth      = NextInt(),
                    TADepth    = NextInt(),
                    Latitude   = NextDouble(),
                    Longitude  = NextDouble(),
                    DetPath    = Decode(NextBytes(10)),
                    Speed      = NextDouble(),
                    Situation  = Decode(NextBytes(10)),
                    WaveHeight = NextDouble(),
                    RgnDepth   = NextDouble(),
                };
                NextBytes(2);

                n.TemperatureCount = NextInt();
                NextBytes(2);
                for (int j = 0; j < n.TemperatureCount; j++)
                {
                    n.Temperatures.Add(new SupplementTemperature
                    {
                        SetIndex = i + 1,
                        Index = j + 1,
                        Depth = NextDouble(),
                        Temperature = NextDouble(),
                        Sonic = NextDouble(),
                        Salt = NextDouble(),
                        GradeDepth = NextDouble(),
                        Comment = Decode(NextBytes(80)),
                    });
                    NextBytes(2);
                }
                sheet.NaviSeaInfos.Add(n);
            }

            // NoiseInfo × SetCount
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var ni = new SupplementNoiseInfo { SetIndex = i + 1, MachineCount = NextInt() };
                NextBytes(2);
                for (int j = 0; j < ni.MachineCount; j++)
                {
                    ni.Machines.Add(new SupplementMachine
                    {
                        SetIndex = i + 1,
                        Index = j + 1,
                        MachineName = Decode(NextBytes(20)),
                        RunTimeFr = Decode(NextBytes(14)),
                        RunTimeTo = Decode(NextBytes(14)),
                        OccurenceFreq = NextDouble(),
                        Comment = Decode(NextBytes(80)),
                    });
                    NextBytes(2);
                }
                sheet.NoiseInfos.Add(ni);
            }

            // TargetInfo × SetCount
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var ti = new SupplementTargetInfo { SetIndex = i + 1, TargetCount = NextInt() };
                NextBytes(2);
                for (int j = 0; j < ti.TargetCount; j++)
                {
                    var t = new SupplementTarget
                    {
                        SetIndex = i + 1,
                        TargetNumber = NextInt(),
                        ContactRecTimeFr = Decode(NextBytes(14)),
                        ContactRecTimeTo = Decode(NextBytes(14)),
                        ClassifyMethod = Decode(NextBytes(10)),
                        DetSonar = Decode(NextBytes(20)),
                        Sensitivity = Decode(NextBytes(10)),
                        Preamplifier = Decode(NextBytes(10)),
                        AGC = Decode(NextBytes(10)),
                        RecmediaNumber = Decode(NextBytes(10)),
                        AFTFWD = Decode(NextBytes(5)),
                        TrackChannel = Decode(NextBytes(5)),
                        RawsignalNumber = Decode(NextBytes(10)),
                        RawsignalName = Decode(NextBytes(80)),
                        FrequencyBand = NextDouble(),
                        SampleRate = NextDouble(),
                        Nation = Decode(NextBytes(20)),
                        TargetType = Decode(NextBytes(10)),
                        TargetClass = Decode(NextBytes(20)),
                        TargetName = Decode(NextBytes(20)),
                        NaviMode = Decode(NextBytes(10)),
                        TargetDistance = NextDouble(),
                        TargetSpeed = NextDouble(),
                        EngineType = Decode(NextBytes(10)),
                        DetsignalType = Decode(NextBytes(5)),
                    };
                    ti.Targets.Add(t);
                    NextBytes(2);
                }
                sheet.TargetInfos.Add(ti);
            }

            // NBDetectionInfo × SetCount
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var nbi = new SupplementNbDetectionInfo { SetIndex = i + 1, NbDetinfoCount = NextInt() };
                NextBytes(2);
                for (int j = 0; j < nbi.NbDetinfoCount; j++)
                {
                    var d = new SupplementNbDetection
                    {
                        SetIndex = i + 1,
                        TargetNumber = NextInt(),
                        DetTimeFr = Decode(NextBytes(14)),
                        DetTimeTo = Decode(NextBytes(14)),
                        DetVesselClass = Decode(NextBytes(20)),
                        DetVesselName = Decode(NextBytes(20)),
                        TargetClassify = Decode(NextBytes(10)),
                        Distance = NextDouble(),
                        Speed = NextDouble(),
                        AnalysisResultFile = Decode(NextBytes(80)),

                        FeatureFreq = NextDouble(),
                        SuperiorFreq = NextDouble(),
                        CylinderCount = NextInt(),
                        BR = NextDouble(),
                        PSR = NextDouble(),
                        PSRPM = NextDouble(),
                        TSR = NextDouble(),
                        CFR = NextDouble(),
                        CSR = NextDouble(),
                        EFR = NextDouble(),
                        ERPM = NextDouble(),
                        Machine = Decode(NextBytes(20)),
                        SWATH = NextDouble(),
                        OddSignal = Decode(NextBytes(20)),
                        LP = NextDouble(),
                        Pitch = NextDouble(),
                        DetSide = Decode(NextBytes(10)),
                        Comment = Decode(NextBytes(80)),
                    };
                    NextBytes(2);

                    d.IndexCount = NextInt();
                    NextBytes(2);

                    for (int k = 0; k < d.IndexCount; k++)
                    {
                        d.Indices.Add(new SupplementNbIndex
                        {
                            SetIndex = i + 1,
                            TargetNumber = d.TargetNumber,
                            Index = NextInt(),
                            CenterFreq = NextDouble(),
                            NormalFreq = NextDouble(),
                            Harmonic = NextInt(),
                            NoiseSrc = Decode(NextBytes(20)),
                        });
                        NextBytes(2);
                    }
                    nbi.NbDetections.Add(d);
                }
                sheet.NbDetectionInfos.Add(nbi);
            }

            return sheet;
        }
    }
}
