using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public static class TmpFileReader
    {
        private const int TR_SEGMENT_RESULT_SIZE = 636;

        private static bool TryRead(BinaryReader br, out int value)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < 4) { value = 0; return false; }
            value = br.ReadInt32();
            return true;
        }

        private static bool TryRead(BinaryReader br, out float value)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < 4) { value = 0f; return false; }
            value = br.ReadSingle();
            return true;
        }

        private static bool TryRead(BinaryReader br, out double value)
        {
            if (br.BaseStream.Length - br.BaseStream.Position < 8) { value = 0.0; return false; }
            value = br.ReadDouble();
            return true;
        }

        
        public static List<TrTmpEventResult> ReadTRResultListFile(string filePath)
        {
            var result = new List<TrTmpEventResult>();

            if (!File.Exists(filePath))
            {
                Debug.WriteLine("[ERR ] TR tmp not found: " + filePath);
                return result;
            }

            Encoding enc = Encoding.GetEncoding(949);

            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs, enc, leaveOpen: false);

            if (fs.Length < 4) return result;

            int eventCount = br.ReadInt32();
            if (eventCount < 0 || eventCount > 1_000_000)
            {
                Debug.WriteLine("[ERR ] TR tmp: invalid eventCount=" + eventCount);
                return result;
            }

            for (int i = 0; i < eventCount; i++)
            {
                if (fs.Length - fs.Position < 12) break;

                // TR_EVENT_RESULT header
                int eventId   = br.ReadInt32();  // long m_lEventID        (C++ long = 4 bytes)
                int confirmed = br.ReadInt32();  // long m_lConfirmedCount (C++ long = 4 bytes)
                int segCount  = br.ReadInt32();  // long m_lSegmentCount   (C++ long = 4 bytes)

                var ev = new TrTmpEventResult
                {
                    EventId        = eventId,
                    ConfirmedCount = confirmed,
                    SegmentCount   = segCount
                };

                // Only confirmed segments were written (m_iConfirmedItem != 0 check in C++)
                long need = (long)confirmed * TR_SEGMENT_RESULT_SIZE;
                if (fs.Length - fs.Position < need)
                {
                    Debug.WriteLine("[ERR ] TR tmp: event[" + i + "] not enough bytes for " + confirmed + " segments");
                    break;
                }

                for (int j = 0; j < confirmed; j++)
                {
                    var seg = ReadTrSegmentResult(br, enc);
                    if (seg != null)
                        ev.Segments.Add(seg);
                }

                result.Add(ev);
            }

            return result;
        }

        // Reads one TR_SEGMENT_RESULT (636 bytes) from the stream.
        private static TrTmpSegmentResult? ReadTrSegmentResult(BinaryReader br, Encoding enc)
        {
            long startPos = br.BaseStream.Position;
            if (br.BaseStream.Length - startPos < TR_SEGMENT_RESULT_SIZE) return null;

            // offset 0: long tr_segment_id (4)
            int segmentId = br.ReadInt32();
            // offset 4: int m_iTRResultItemCount (4)
            int itemCount = br.ReadInt32();
            // offset 8: int m_iConfirmedItem (4)
            int confirmedItem = br.ReadInt32();
            // offset 12: char m_strSourceName[31] (31)
            string sourceName = ReadFixedString(br, enc, 31);
            // offset 43: byte m_byteType (1)
            byte byteType = br.ReadByte();
            // offset 44: char m_strComment[81] (81)
            string comment = ReadFixedString(br, enc, 81);
            // offset 125: pad 3 bytes (align float to 4)
            br.ReadBytes(3);
            // offset 128: float match_rate (4)
            float matchRate = br.ReadSingle();
            // offset 132: char m_strAudioSnippetFileName[81] (81)
            string audioSnippet = ReadFixedString(br, enc, 81);
            // offset 213: pad 3 bytes (align BOOL/int to 4)
            br.ReadBytes(3);
            // offset 216: BOOL m_bIsMergedCls (4)
            bool isMergedCls = br.ReadInt32() != 0;
            // offset 220: TR_RESULT_ITEM m_TRResultItem[8] (8 × 52 = 416)
            var items = new TrResultItem[8];
            for (int k = 0; k < 8; k++)
                items[k] = ReadTrResultItem(br, enc);

            // Clamp itemCount to valid range
            itemCount = Math.Clamp(itemCount, 0, 8);

            // confirmedItem: 1~8 → index (confirmedItem-1), -1 → UNKNOWN, -2 → 운용자확정
            TrResultItem? confirmedResult = null;
            if (confirmedItem >= 1 && confirmedItem <= 8)
                confirmedResult = items[confirmedItem - 1];

            return new TrTmpSegmentResult
            {
                SegmentId            = segmentId,
                ConfirmedItem        = confirmedItem,
                SourceName           = sourceName,
                Type                 = byteType,
                Comment              = comment,
                MatchRate            = matchRate,
                AudioSnippetFileName = audioSnippet,
                IsMergedCls          = isMergedCls,
                ConfirmedResultItem  = confirmedResult
            };
        }

        // TR_RESULT_ITEM (52 bytes)
        private static TrResultItem ReadTrResultItem(BinaryReader br, Encoding enc)
        {
            // offset 0: long db_tr_seg_id (4)
            int dbTrSegId = br.ReadInt32();
            // offset 4: char tr_source_name[30] (30)
            string srcName = ReadFixedString(br, enc, 30);
            // offset 34: byte tr_type (1)
            byte trType = br.ReadByte();
            // offset 35: pad 1 byte (align float to 4)
            br.ReadByte();
            // offset 36: float score (4)
            float score = br.ReadSingle();
            // offset 40: float match_rate (4)
            float matchRate = br.ReadSingle();
            // offset 44: int rank (4)
            int rank = br.ReadInt32();
            // offset 48: BOOL b_IsConfirmed (4)
            bool isConfirmed = br.ReadInt32() != 0;
            // total = 52

            return new TrResultItem
            {
                DbTrSegId   = dbTrSegId,
                SourceName  = srcName,
                Type        = trType,
                Score       = score,
                MatchRate   = matchRate,
                Rank        = rank,
                IsConfirmed = isConfirmed
            };
        }

        private static string ReadFixedString(BinaryReader br, Encoding enc, int bytes)
        {
            var buf = br.ReadBytes(bytes);
            return enc.GetString(buf).TrimEnd('\0', ' ');
        }

        public static List<AI_EVENT_RESULT> ReadAIResultListFile(string filePath)
        {
            var result = new List<AI_EVENT_RESULT>();
            if (!File.Exists(filePath))
            {
                Debug.WriteLine("[ERR ] AI 파일이 없습니다: " + filePath);
                return result;
            }

            using var br = new BinaryReader(File.Open(filePath, FileMode.Open, FileAccess.Read));
            if (!TryRead(br, out int eventCount))
            {
                Debug.WriteLine("[ERR ] AI 이벤트 개수 읽기 실패");
                return result;
            }

            // C++ long on Windows (MSVC) = 4 bytes. C# int = 4 bytes.
            Encoding enc = Encoding.GetEncoding(949);

            for (int i = 0; i < eventCount; i++)
            {
                var ev = new AI_EVENT_RESULT();

                // long m_lEventID (C++ long = 4 bytes)
                if (!TryRead(br, out int tmpEventID))
                {
                    Debug.WriteLine("[WARN] AI[" + i + "] EventID 누락, 중단");
                    break;
                }
                ev.EventID = tmpEventID;

                // char m_ConfirmedSonarName[30+1] = 31 bytes
                byte[] buf31 = br.ReadBytes(31);
                ev.ConfirmedSonarName = enc.GetString(buf31).TrimEnd('\0', ' ');
                // char m_ConfirmedPulseName[30+1] = 31 bytes
                byte[] buf31b = br.ReadBytes(31);
                ev.ConfirmedPulseName = enc.GetString(buf31b).TrimEnd('\0', ' ');
                // char m_ConfirmedComment[80+1] = 81 bytes
                byte[] buf81 = br.ReadBytes(81);
                ev.ConfirmedComment = enc.GetString(buf81).TrimEnd('\0', ' ');

                // float m_ConfirmedScore
                if (!TryRead(br, out float tmpScore))
                {
                    Debug.WriteLine("[WARN] AI[" + i + "] ConfirmedScore 누락");
                    break;
                }
                ev.ConfirmedScore = tmpScore;

                // int m_iConfirmMode
                if (!TryRead(br, out int tmpMode))
                {
                    Debug.WriteLine("[WARN] AI[" + i + "] ConfirmMode 누락");
                    break;
                }
                ev.ConfirmMode = tmpMode;

                // long m_lConfirmedDBPulseID (C++ long = 4 bytes)
                if (!TryRead(br, out int tmpDBPulseID))
                {
                    Debug.WriteLine("[WARN] AI[" + i + "] ConfirmedDBPulseID 누락");
                    break;
                }
                ev.ConfirmedDBPulseID = tmpDBPulseID;

                // 후보 읽기 (only when ConfirmMode == 1 or 2)
                if (ev.ConfirmMode == 1 || ev.ConfirmMode == 2)
                {
                    var cand = new AI_RESULT_CANDIDATE();

                    // long m_lDBPulseID (C++ long = 4 bytes)
                    if (!TryRead(br, out int tmpCandID))
                    {
                        Debug.WriteLine("[WARN] AI[" + i + "] Candidate DBPulseID 누락");
                        break;
                    }
                    cand.DBPulseID = tmpCandID;

                    // char m_PulseName[30+1] = 31 bytes
                    byte[] buf31c = br.ReadBytes(31);
                    cand.PulseName = enc.GetString(buf31c).TrimEnd('\0', ' ');
                    // char m_SonarName[30+1] = 31 bytes
                    byte[] buf31d = br.ReadBytes(31);
                    cand.SonarName = enc.GetString(buf31d).TrimEnd('\0', ' ');

                    // double m_dScore
                    if (!TryRead(br, out double tmpCandScore))
                    {
                        Debug.WriteLine("[WARN] AI[" + i + "] Candidate Score 누락");
                        break;
                    }
                    cand.Score = tmpCandScore;

                    // int m_iRank
                    if (!TryRead(br, out int tmpRank))
                    {
                        Debug.WriteLine("[WARN] AI[" + i + "] Candidate Rank 누락");
                        break;
                    }
                    cand.Rank = tmpRank;

                    // int m_iSegmentCount
                    if (!TryRead(br, out int tmpSegCount))
                    {
                        Debug.WriteLine("[WARN] AI[" + i + "] Candidate SegmentCount 누락");
                        break;
                    }
                    cand.SegmentCount = tmpSegCount;

                    // BOOL m_bIsSelected (C++ BOOL = int = 4 bytes)
                    if (!TryRead(br, out int tmpIsSel))
                    {
                        Debug.WriteLine("[WARN] AI[" + i + "] Candidate IsSelected 누락");
                        break;
                    }
                    cand.IsSelected = tmpIsSel != 0;

                    // int iSegCnt (separately written count)
                    if (!TryRead(br, out int segCount))
                        segCount = 0;

                    for (int j = 0; j < segCount; j++)
                    {
                        // sizeof(AI_SEGMENT_RESULT): long(4)+long(4)+int(4)+double(8) = 20 bytes
                        // MSVC default alignment: double at offset 12 needs 8-byte alignment → 4 bytes padding after int
                        // Actual: 4+4+4+4(pad)+8 = 24 bytes (or 20 if no padding). Read field by field.
                        var seg = new AI_SEGMENT_RESULT();

                        // long m_lDBSegmentID (C++ long = 4 bytes)
                        if (!TryRead(br, out int tmpDbSegID))
                        {
                            Debug.WriteLine("[WARN] AI[" + i + "] Seg[" + j + "] DbSegmentID 누락");
                            break;
                        }
                        seg.DbSegmentID = tmpDbSegID;

                        // long m_lSegmentID (C++ long = 4 bytes)
                        if (!TryRead(br, out int tmpSegID))
                        {
                            Debug.WriteLine("[WARN] AI[" + i + "] Seg[" + j + "] SegmentID 누락");
                            break;
                        }
                        seg.SegmentID = tmpSegID;

                        // int m_iRank
                        if (!TryRead(br, out int tmpSegRank))
                        {
                            Debug.WriteLine("[WARN] AI[" + i + "] Seg[" + j + "] Rank 누락");
                            break;
                        }
                        seg.Rank = tmpSegRank;

                        // double m_dScore (8-byte aligned: skip 4-byte padding after int if needed)
                        // cfResultFile.Write(pSegRet, sizeof(AI_SEGMENT_RESULT)) writes the whole struct raw.
                        // MSVC /Zp default: double aligns to 8 bytes → 4 bytes padding between m_iRank and m_dScore.
                        br.ReadBytes(4); // alignment padding

                        if (!TryRead(br, out double tmpSegScore))
                        {
                            Debug.WriteLine("[WARN] AI[" + i + "] Seg[" + j + "] Score 누락");
                            break;
                        }
                        seg.Score = tmpSegScore;

                        cand.listSegmentResult.Add(seg);
                    }

                    ev.listCandidate.Add(cand);
                }

                result.Add(ev);
            }

            return result;
        }
    }
}