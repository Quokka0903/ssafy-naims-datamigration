using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.Supplement
{
    public class AlgReader
    {
        public static SupplementSheet ReadSupplementAlg(string filePath, Encoding? enc = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

            enc ??= Encoding.GetEncoding(949);  // CP949 인코딩을 사용하여 읽기

            using var sr = new StreamReader(filePath, enc, detectEncodingFromByteOrderMarks: true);

            // 데이터 읽기 함수 정의 (바이트 단위로 읽기)
            byte[] NextBytes(int length)
            {
                var buffer = new byte[length];
                int bytesRead = sr.BaseStream.Read(buffer, 0, length); // 바이트 단위로 읽기

                if (bytesRead < length)
                {
                    // 예상보다 덜 읽은 경우, 읽은 만큼만 반환
                    if (bytesRead == 0) throw new EndOfStreamException("Unexpected EOF while reading .alg");
                    return buffer.Take(bytesRead).ToArray();
                }

                return buffer;
            }


            string DecodeBytes(byte[] buffer)
            {
                return enc.GetString(buffer).Trim(); // TrimEnd로 마지막 공백만 제거
            }

            int NextInt()
            {
                var value = DecodeBytes(NextBytes(8));  // 정수는 9바이트로 읽음
                return int.TryParse(value, out var result) ? result : 0;
            }

            double NextDouble()
            {
                var value = DecodeBytes(NextBytes(16)); // 실수는 17바이트로 읽음
                return double.TryParse(value, out var result) ? result : 0.0;
            }

            var sheet = new SupplementSheet();

            // ---- MissionInfo ----
            sheet.MissionInfo = new SupplementMissionInfo
            {
                DetPlatform = DecodeBytes(NextBytes(30)),
                DetVesselClass = DecodeBytes(NextBytes(20)),
                DetVesselName = DecodeBytes(NextBytes(20)),
                MissionDateFr = DecodeBytes(NextBytes(8)),
                MissionDateTo = DecodeBytes(NextBytes(8)),
                MissionSeaArea = DecodeBytes(NextBytes(20)),
                MissionSummary = DecodeBytes(NextBytes(80)),
                MissionComment = DecodeBytes(NextBytes(80)),
            };

            NextBytes(2); // 개행 스킵

            // ---- Recmedia ----
            int recmediaCount = NextInt();
            NextBytes(2); // 개행 스킵

            for (int i = 0; i < recmediaCount; i++)
            {
                var recmedia = new SupplementRecmedia
                {
                    RecordCount = NextInt()
                };

                NextBytes(2); // 개행 스킵

                for (int j = 0; j < recmedia.RecordCount; j++)
                {
                    recmedia.Records.Add(new SupplementRecord
                    {
                        RecordTimeFr = DecodeBytes(NextBytes(14)),  // 정확한 길이만큼 읽기
                        RecordTimeTo = DecodeBytes(NextBytes(14)),
                        RecordComment = DecodeBytes(NextBytes(80)),
                    });

                    NextBytes(2); // 개행 스킵
                }

                sheet.Recmedia.Add(recmedia);
            }

            // ---- SetCount ----
            sheet.SetCount = NextInt();
            NextBytes(2); // 개행 스킵

            // ---- NaviSeaInfo (SetCount번) ----
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var n = new SupplementNaviSeaInfo
                {
                    DetDate = DecodeBytes(NextBytes(8)),
                    DetTime = DecodeBytes(NextBytes(8)),
                    Depth = NextInt(),
                    TADepth = NextInt(),
                    Latitude = NextDouble(),
                    Longitude = NextDouble(),
                    DetPath = DecodeBytes(NextBytes(10)),
                    Speed = NextDouble(),
                    Situation = DecodeBytes(NextBytes(10)),
                    WaveHeight = NextDouble(),
                    RgnDepth = NextDouble(),
                };

                NextBytes(2); // 개행 스킵

                n.TemperatureCount = NextInt();
                NextBytes(2); // 개행 스킵

                for (int j = 0; j < n.TemperatureCount; j++)
                {
                    n.Temperatures.Add(new SupplementTemperature
                    {
                        Depth = NextDouble(),
                        Temperature = NextDouble(),
                        Sonic = NextDouble(),
                        Salt = NextDouble(),
                        GradeDepth = NextDouble(),
                        Comment = DecodeBytes(NextBytes(80)),
                    });

                    NextBytes(2); // 개행 스킵
                }

                sheet.NaviSeaInfos.Add(n);
            }

            // ---- NoiseInfo (SetCount번) ----
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var ni = new SupplementNoiseInfo
                {
                    MachineCount = NextInt()
                };

                NextBytes(2); // 개행 스킵

                for (int j = 0; j < ni.MachineCount; j++)
                {
                    ni.Machines.Add(new SupplementMachine
                    {
                        MachineName = DecodeBytes(NextBytes(20)),
                        RunTimeFr = DecodeBytes(NextBytes(14)),
                        RunTimeTo = DecodeBytes(NextBytes(14)),
                        OccurenceFreq = NextDouble(),
                        Comment = DecodeBytes(NextBytes(80)),
                    });

                    NextBytes(2); // 개행 스킵
                }

                sheet.NoiseInfos.Add(ni);
            }

            // ---- TargetInfo (SetCount번) ----
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var ti = new SupplementTargetInfo
                {
                    TargetCount = NextInt()
                };

                NextBytes(2); // 개행 스킵

                for (int j = 0; j < ti.TargetCount; j++)
                {
                    var t = new SupplementTarget
                    {
                        TargetNumber = NextInt(),
                        ContactRecTimeFr = DecodeBytes(NextBytes(14)),
                        ContactRecTimeTo = DecodeBytes(NextBytes(14)),
                        ClassifyMethod = DecodeBytes(NextBytes(10)),
                        DetSonar = DecodeBytes(NextBytes(20)),
                        Sensitivity = DecodeBytes(NextBytes(10)),
                        Preamplifier = DecodeBytes(NextBytes(10)),
                        AGC = DecodeBytes(NextBytes(10)),
                        RecmediaNumber = DecodeBytes(NextBytes(10)),
                        AFTFWD = DecodeBytes(NextBytes(5)),
                        TrackChannel = DecodeBytes(NextBytes(5)),
                        RawsignalNumber = DecodeBytes(NextBytes(10)),
                        RawsignalName = DecodeBytes(NextBytes(80)),
                        FrequencyBand = NextDouble(),
                        SampleRate = NextDouble(),
                        Nation = DecodeBytes(NextBytes(20)),
                        TargetType = DecodeBytes(NextBytes(10)),
                        TargetClass = DecodeBytes(NextBytes(20)),
                        TargetName = DecodeBytes(NextBytes(20)),
                        NaviMode = DecodeBytes(NextBytes(10)),
                        TargetDistance = NextDouble(),
                        TargetSpeed = NextDouble(),
                        EngineType = DecodeBytes(NextBytes(10)),
                        DetsignalType = DecodeBytes(NextBytes(5)),
                    };
                    ti.Targets.Add(t);

                    NextBytes(2); // 개행 스킵
                }

                sheet.TargetInfos.Add(ti);
            }

            // ---- NBDetectionInfo (SetCount번) ----
            for (int i = 0; i < sheet.SetCount; i++)
            {
                var nbi = new SupplementNbDetectionInfo
                {
                    NbDetinfoCount = NextInt()
                };

                NextBytes(2); // 개행 스킵

                for (int j = 0; j < nbi.NbDetinfoCount; j++)
                {
                    var d = new SupplementNbDetection
                    {
                        TargetNumber = NextInt(),
                        DetTimeFr = DecodeBytes(NextBytes(14)),
                        DetTimeTo = DecodeBytes(NextBytes(14)),
                        DetVesselClass = DecodeBytes(NextBytes(20)),
                        DetVesselName = DecodeBytes(NextBytes(20)),
                        TargetClassify = DecodeBytes(NextBytes(10)),
                        Distance = NextDouble(),
                        Speed = NextDouble(),
                        AnalysisResultFile = DecodeBytes(NextBytes(80)),

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
                        Machine = DecodeBytes(NextBytes(20)),
                        SWATH = NextDouble(),
                        OddSignal = DecodeBytes(NextBytes(20)),
                        LP = NextDouble(),
                        Pitch = NextDouble(),
                        DetSide = DecodeBytes(NextBytes(10)),
                        Comment = DecodeBytes(NextBytes(80)),
                    };

                    NextBytes(2); // 개행 스킵

                    d.IndexCount = NextInt();

                    NextBytes(2); // 개행 스킵

                    for (int k = 0; k < d.IndexCount; k++)
                    {
                        d.Indices.Add(new SupplementNbIndex
                        {
                            Index = NextInt(),
                            CenterFreq = NextDouble(),
                            NormalFreq = NextDouble(),
                            Harmonic = NextInt(),
                            NoiseSrc = DecodeBytes(NextBytes(20)),
                        });

                        NextBytes(2); // 개행 스킵
                    }

                    nbi.NbDetections.Add(d);
                }

                sheet.NbDetectionInfos.Add(nbi);
            }

            return sheet;
        }


        private static string Clean(string s)
            => (s ?? "").Replace("\0", "").Trim();

        private static bool TryParseInt(string s, out int v)
            => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        private static bool TryParseDouble(string s, out double v)
            => double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out v);
    }
}
