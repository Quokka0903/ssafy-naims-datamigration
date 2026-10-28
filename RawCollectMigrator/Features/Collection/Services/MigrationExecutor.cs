using System.IO;
using Microsoft.Data.SqlClient;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.Collection
{
    public sealed class MigrationProgress
    {
        public int TotalCollections { get; set; }
        public int DoneCollections { get; set; }
        public string CurrentFolder { get; set; } = "";
        public string Phase { get; set; } = "";
    }

    /// <summary>실행 옵션.</summary>
    public sealed class MigrationOptions
    {
        /// <summary>true 면 실제 INSERT 없이 검증만 수행하고 롤백한다.</summary>
        public bool DryRun { get; set; }

        /// <summary>RAW_SIG_NAME 이 이미 있는 임무폴더는 건너뛴다.</summary>
        public bool SkipDuplicates { get; set; } = true;

        // [NAIMS-ISSUE:WAV-SIZE-LIMIT] 대용량 WAV 건너뛰기 임계(512MB)
        /// <summary>이 크기를 넘는 WAV 는 건너뛴다(0이면 제한 없음). FILESTREAM 전체 로드 방식이라 메모리 보호용.</summary>
        public long MaxWavBytes { get; set; } = 512L * 1024 * 1024;
    }

    /// <summary>사전 점검 결과.</summary>
    public sealed class PreflightReport
    {
        public bool CanProceed { get; set; }
        public List<string> Ok { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Ok)       sb.AppendLine("  ✓ " + s);
            foreach (var s in Warnings) sb.AppendLine("  ⚠ " + s);
            foreach (var s in Errors)   sb.AppendLine("  ✗ " + s);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 선택된 CollectionRow들에 대해 RAW_COLLECT_SIG / RAW_COLLECT_SUPPLEMENT / RAW_SCHANNEL_INFO 삽입.
    /// 각 임무폴더 단위로 트랜잭션 처리. 한 폴더 실패 시 해당 폴더만 롤백, 다음 폴더로 진행.
    ///
    /// 타겟 스키마 실측(2026-07-21, NAIMS2 192.168.200.205):
    ///   - RECORD_ID 는 IDENTITY, FK 는 REF_RAW_COLLECT_SIG_ID → RAW_COLLECT_SIG.RECORD_ID 2건뿐
    ///   - DET_SONAR / MISSION_SEA_AREA 는 FK 가 아니라 단순 int NOT NULL
    ///   - SUPPLEMENT_DATA / CHANNEL_AUDIO_FILE 은 FILESTREAM → 반드시 트랜잭션 안에서 써야 한다
    /// </summary>
    public static class MigrationExecutor
    {
        /// <summary>
        /// 실행 전 점검: 연결·테이블 접근·쓰기 권한·중복·파일 크기를 확인한다.
        /// </summary>
        public static async Task<PreflightReport> PreflightAsync(
            string connectionString,
            IEnumerable<CollectionRow> rows,
            MigrationOptions opt,
            CancellationToken ct = default)
        {
            var rep = new PreflightReport();
            var list = rows.ToList();

            try
            {
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct);
                rep.Ok.Add($"DB 연결 성공 ({conn.DataSource} / {conn.Database})");

                // 대상 테이블 존재 + 현재 건수
                foreach (var t in new[] { "RAW_COLLECT_SIG", "RAW_COLLECT_SUPPLEMENT", "RAW_SCHANNEL_INFO" })
                {
                    using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM [{t}]", conn);
                    var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                    rep.Ok.Add($"{t} 접근 가능 — 현재 {n:N0}건");
                }

                // 쓰기 권한: 롤백되는 트랜잭션으로 확인
                try
                {
                    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                    using (var cmd = new SqlCommand(
                        "INSERT INTO RAW_COLLECT_SIG (RAW_SIG_NAME,RAW_SIG_TYPE,RECORDING_DATETIME,RECORDING_SYSTEM," +
                        "RECORDING_VESSEL,IMPORT_DATETIME,MISSION_DATETIME,PROCESSING_STATE,ACCESS_LEVEL,FILE_SIZE," +
                        "LINKED_PATH,LINKED_MEDIA,CHNG_DATETIME,DELETED,IS_TRAINING,IS_LEGACY) " +
                        "VALUES ('__preflight__',1,SYSDATETIME(),'ETC','',SYSDATETIME(),SYSDATETIME(),0,0,'0',0,'Y',SYSDATETIME(),0,0,1)",
                        conn, tx))
                    {
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await tx.RollbackAsync(ct);
                    rep.Ok.Add("INSERT 권한 확인 (시험 삽입 후 롤백)");
                }
                catch (Exception ex)
                {
                    rep.Errors.Add("INSERT 권한 없음 또는 스키마 불일치: " + ex.Message);
                }

                // 중복 검사
                var dup = new List<string>();
                foreach (var r in list)
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = new SqlCommand(
                        "SELECT COUNT(*) FROM RAW_COLLECT_SIG WHERE RAW_SIG_NAME = @n", conn);
                    cmd.Parameters.AddWithValue("@n", Truncate(r.MissionName, 150));
                    if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0) dup.Add(r.MissionName);
                }
                if (dup.Count > 0)
                {
                    var head = string.Join(", ", dup.Take(3));
                    var more = dup.Count > 3 ? $" 외 {dup.Count - 3}건" : "";
                    if (opt.SkipDuplicates)
                        rep.Warnings.Add($"이미 이관된 폴더 {dup.Count}건 — 건너뜁니다 ({head}{more})");
                    else
                        rep.Warnings.Add($"이미 이관된 폴더 {dup.Count}건 — 중복 삽입됩니다 ({head}{more})");
                }
                else rep.Ok.Add("중복 없음 — 모두 신규");
            }
            catch (Exception ex)
            {
                rep.Errors.Add("DB 점검 실패: " + ex.Message);
            }

            // 파일 점검
            long totalWav = 0;
            int oversize = 0, missing = 0, wavCount = 0;
            foreach (var r in list)
            {
                if (!r.HasSupplementAlg) rep.Warnings.Add($"{r.MissionName}: 보조정보.alg 없음 — SUPPLEMENT 생략");
                foreach (var w in r.WavFiles)
                {
                    wavCount++;
                    try
                    {
                        var fi = new FileInfo(w);
                        if (!fi.Exists) { missing++; continue; }
                        totalWav += fi.Length;
                        if (opt.MaxWavBytes > 0 && fi.Length > opt.MaxWavBytes) oversize++;
                    }
                    catch { missing++; }
                }
            }
            rep.Ok.Add($"대상 {list.Count}개 폴더 · WAV {wavCount}개 · 총 {totalWav / 1024.0 / 1024:F1} MB");
            if (missing  > 0) rep.Errors.Add($"접근 불가 WAV {missing}개");
            if (oversize > 0) rep.Warnings.Add($"{opt.MaxWavBytes / 1024 / 1024}MB 초과 WAV {oversize}개 — 건너뜁니다");

            rep.CanProceed = rep.Errors.Count == 0;
            return rep;
        }

        public static async Task RunAsync(
            string connectionString,
            IEnumerable<CollectionRow> rows,
            IProgress<MigrationProgress>? progress,
            CancellationToken ct,
            MigrationOptions? options = null)
        {
            var opt = options ?? new MigrationOptions();

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            var list = rows.ToList();
            var prog = new MigrationProgress { TotalCollections = list.Count };

            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = list[i];

                prog.DoneCollections = i;
                prog.CurrentFolder = row.MissionName;
                prog.Phase = "시작";
                progress?.Report(prog);

                row.Status = "진행중";
                row.ErrorMessage = "";

                // 중복 건너뛰기
                if (opt.SkipDuplicates && await ExistsAsync(conn, row.MissionName, ct))
                {
                    row.Status = "건너뜀";
                    row.ErrorMessage = "이미 이관된 임무폴더 (RAW_SIG_NAME 중복)";
                    continue;
                }

                using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                try
                {
                    // 0) 보조정보.alg 우선 파싱 → RAW_COLLECT_SIG의 RECORDING_DATETIME/SYSTEM/VESSEL에 사용
                    AlgInfo? alg = null;
                    if (row.HasSupplementAlg && row.SupplementAlgPath != null)
                    {
                        prog.Phase = "보조정보.alg 파싱";
                        progress?.Report(prog);
                        alg = AlgParser.TryParse(row.SupplementAlgPath);
                    }

                    // 1) RAW_COLLECT_SIG INSERT → RECORD_ID 획득
                    prog.Phase = "RAW_COLLECT_SIG";
                    progress?.Report(prog);
                    int collectSigId = await InsertRawCollectSigAsync(conn, tx, row, alg, ct);
                    row.InsertedCollectSigId = collectSigId;

                    // 2) RAW_COLLECT_SUPPLEMENT INSERT (보조정보.alg 있으면)
                    int suppCount = 0;
                    if (row.HasSupplementAlg && row.SupplementAlgPath != null)
                    {
                        prog.Phase = "RAW_COLLECT_SUPPLEMENT";
                        progress?.Report(prog);
                        suppCount = await InsertSupplementAsync(conn, tx, collectSigId, row.SupplementAlgPath, ct);
                    }
                    row.InsertedSupplementCount = suppCount;

                    // 3) RAW_SCHANNEL_INFO INSERT (Signal_*.wav 각각)
                    int schCount = 0, skipped = 0;
                    foreach (var wav in row.WavFiles)
                    {
                        ct.ThrowIfCancellationRequested();

                        // FILESTREAM 컬럼에 전체 바이트를 올리므로 과대 파일은 메모리 보호를 위해 건너뛴다.
                        if (opt.MaxWavBytes > 0)
                        {
                            long size = 0;
                            try { size = new FileInfo(wav).Length; } catch { }
                            if (size > opt.MaxWavBytes)
                            {
                                skipped++;
                                continue;
                            }
                        }

                        prog.Phase = $"RAW_SCHANNEL_INFO ({schCount + 1}/{row.WavCount})";
                        progress?.Report(prog);
                        await InsertSchannelInfoAsync(conn, tx, collectSigId, wav, ct);
                        schCount++;
                    }
                    row.InsertedSchannelCount = schCount;

                    if (opt.DryRun)
                    {
                        await tx.RollbackAsync(ct);
                        row.Status = "시험완료";
                        row.ErrorMessage = $"DRY RUN — 검증만 수행, 롤백됨 (SIG 1, SUPP {suppCount}, SCH {schCount}"
                                         + (skipped > 0 ? $", 크기초과 {skipped}건 제외" : "") + ")";
                    }
                    else
                    {
                        await tx.CommitAsync(ct);
                        row.Status = "완료";
                        if (skipped > 0)
                            row.ErrorMessage = $"WAV {skipped}건은 크기 초과로 제외됨";
                    }
                }
                catch (OperationCanceledException)
                {
                    try { await tx.RollbackAsync(ct); } catch { }
                    row.Status = "취소";
                    throw;
                }
                catch (Exception ex)
                {
                    try { await tx.RollbackAsync(CancellationToken.None); } catch { }
                    row.Status = "실패";
                    row.ErrorMessage = ex.Message;
                }
            }

            prog.DoneCollections = list.Count;
            prog.Phase = "완료";
            prog.CurrentFolder = "";
            progress?.Report(prog);
        }

        /// <summary>
        /// RAW_SIG_NAME 기준으로 이미 이관된 임무폴더인지 확인.
        /// [NAIMS-ISSUE:DUP-CHECK-SWALLOW] 예외를 삼켜 false("미이관")로 답하면 안 된다 —
        /// DB 순단 한 번에 멱등 장치가 무력화되어 같은 폴더가 중복 적재된다.
        /// 조회에 실패하면 예외를 올려 해당 폴더를 "오류"로 남기고 넘어간다.
        /// </summary>
        private static async Task<bool> ExistsAsync(SqlConnection conn, string missionName, CancellationToken ct)
        {
            using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM RAW_COLLECT_SIG WHERE RAW_SIG_NAME = @n", conn);
            cmd.Parameters.AddWithValue("@n", Truncate(missionName, 150));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
        }

        // [NAIMS-ISSUE:CHAR-PADDING] DB 로 나가는 문자열은 예외 없이 LegacyText 를 거친다.
        //   자르기 전에 후행 패딩을 떼야 폭을 낭비하지 않는다.
        private static string Truncate(string s, int max)
        {
            var t = LegacyText.Normalize(s);
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private static async Task<int> InsertRawCollectSigAsync(
            SqlConnection conn, SqlTransaction tx, CollectionRow row, AlgInfo? alg, CancellationToken ct)
        {
            // 매핑 규칙 (사용자 지정):
            //   RAW_SIG_TYPE   = 1 (고정)
            //   RECORDING_*    ← 보조정보.alg 파싱 (없으면 폴더 LastWriteTime/ETC/"")
            //   LINKED_PATH    = 0 (enum 기본값 — 소스에 없는 신규 DB 자체 메타)
            //   LINKED_MEDIA   = 'Y' (사용자 지정)
            //   IS_LEGACY      = 1 (레거시 이관 데이터)
            const string sql = @"
INSERT INTO RAW_COLLECT_SIG
( RAW_SIG_NAME, RAW_SIG_TYPE, RECORDING_DATETIME, RECORDING_SYSTEM, RECORDING_VESSEL,
  IMPORT_DATETIME, MISSION_OPERATOR, MISSION_DATETIME, PROCESSING_STATE, ACCESS_LEVEL,
  FILE_SIZE, LINKED_PATH, LINKED_MEDIA, CHNG_DATETIME, COMMENT, DELETED, IS_TRAINING, IS_LEGACY )
OUTPUT INSERTED.RECORD_ID
VALUES
( @name, @sigType, @recDt, @recSys, @recVessel,
  @impDt, @missOp, @missDt, @procState, @accLvl,
  @fileSize, @linkPath, @linkMedia, @chngDt, @comment, @deleted, @isTraining, @isLegacy );";

            using var cmd = new SqlCommand(sql, conn, tx);
            var now = DateTime.Now;

            DateTime recDt = alg?.RecordingDateTime
                          ?? (row.LastWriteTime == DateTime.MinValue ? now : row.LastWriteTime);
            string recSys = TruncateText(alg?.RecordingSystemMapped ?? "ETC", 30);
            string recVessel = TruncateText(alg?.DetVesselName ?? "", 45);
            DateTime missDt = alg?.RecordingDateTime
                           ?? (row.LastWriteTime == DateTime.MinValue ? now : row.LastWriteTime);

            cmd.Parameters.AddWithValue("@name", TruncateText(row.MissionName, 150));
            cmd.Parameters.AddWithValue("@sigType", 1);
            cmd.Parameters.AddWithValue("@recDt", recDt);
            cmd.Parameters.AddWithValue("@recSys", recSys);
            cmd.Parameters.AddWithValue("@recVessel", recVessel);
            cmd.Parameters.AddWithValue("@impDt", now);
            cmd.Parameters.AddWithValue("@missOp", (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@missDt", missDt);
            cmd.Parameters.AddWithValue("@procState", 0);
            cmd.Parameters.AddWithValue("@accLvl", 0);
            cmd.Parameters.AddWithValue("@fileSize", TruncateText(FormatBytesShort(row.TotalSizeBytes), 10));
            cmd.Parameters.AddWithValue("@linkPath", 0);         // enum int (기본값 0, 필요 시 조정)
            cmd.Parameters.AddWithValue("@linkMedia", "Y");      // varchar(1) 'Y' 고정
            cmd.Parameters.AddWithValue("@chngDt", now);
            cmd.Parameters.AddWithValue("@comment", (object)TruncateText(row.MissionFolderPath, 1000));
            cmd.Parameters.AddWithValue("@deleted", (short)0);
            cmd.Parameters.AddWithValue("@isTraining", 0);
            cmd.Parameters.AddWithValue("@isLegacy", true);       // bit — 레거시 이관 데이터

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }

        private static async Task<int> InsertSupplementAsync(
            SqlConnection conn, SqlTransaction tx, int refCollectSigId, string algPath, CancellationToken ct)
        {
            byte[] algBytes;
            try { algBytes = await File.ReadAllBytesAsync(algPath, ct); }
            catch { return 0; }

            // RAW_COLLECT_SUPPLEMENT (7월본): ROWGUID NOT NULL (DEFAULT newid())
            const string sql = @"
INSERT INTO RAW_COLLECT_SUPPLEMENT
( REF_RAW_COLLECT_SIG_ID, DET_SONAR, MISSION_SEA_AREA, SUPPLEMENT_DATA,
  CHNG_DATETIME, DELETED, IS_TRAINING, ROWGUID )
VALUES
( @refId, @detSonar, @seaArea, @data, @chngDt, @deleted, @isTraining, NEWID() );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@refId", refCollectSigId);
            cmd.Parameters.AddWithValue("@detSonar", 1);              // 사용자 지정: 1 (매핑 테이블 확정 시 수정)
            cmd.Parameters.AddWithValue("@seaArea", 1);               // 사용자 지정: 1
            var p = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
            p.Value = algBytes;
            cmd.Parameters.AddWithValue("@chngDt", DateTime.Now);
            cmd.Parameters.AddWithValue("@deleted", (short)0);
            cmd.Parameters.AddWithValue("@isTraining", 0);

            await cmd.ExecuteNonQueryAsync(ct);
            return 1;
        }

        private static async Task InsertSchannelInfoAsync(
            SqlConnection conn, SqlTransaction tx, int refCollectSigId, string wavPath, CancellationToken ct)
        {
            // WAV 헤더 빠른 읽기 (sample rate, audio length sec)
            var wh = WavHeader.TryRead(wavPath);
            int sampleRate = wh.SampleRate;
            float lengthSec = wh.AudioLengthSec;

            // CHANNEL_AUDIO_FILE 은 varbinary(max) FILESTREAM — WAV 바이너리 자체를 삽입한다.
            // 파일 크기가 수백MB에 달할 수 있으므로 File.ReadAllBytes로 메모리 로드.
            //   (더 큰 파일 대비 SqlFileStream 스트리밍 도입은 Phase 2로 유예)
            byte[] wavBytes;
            try { wavBytes = await File.ReadAllBytesAsync(wavPath, ct); }
            catch (Exception ex)
            {
                throw new IOException($"WAV 파일 읽기 실패: {wavPath}", ex);
            }

            // RAW_SCHANNEL_INFO 스키마 (7월본):
            //   CHANNEL_AUDIO_FILE     varbinary(max) FILESTREAM NULL   ← WAV 바이너리
            //   ROWGUID                uniqueidentifier NOT NULL (DEFAULT newid())
            //   EXTRACT_NB_CNT/SS_CNT  int NULL
            const string sql = @"
INSERT INTO RAW_SCHANNEL_INFO
( REF_RAW_COLLECT_SIG_ID, REF_RAW_FCHANNEL_INFO_ID, EXTRACT_NB_CNT, EXTRACT_SS_CNT,
  CHANNEL_AUDIO_FILE, SAMPLING_RATE, AUDIO_LENGTH, PROCESSING_STATE,
  CHNG_DATETIME, F_TARGET_COMMENT, SS_COMMENT, IS_TRAINING, DELETED, ROWGUID )
VALUES
( @refSig, NULL, 0, 0,
  @wavBytes, @sr, @lenSec, 0,
  @chngDt, NULL, NULL, 0, 0, NEWID() );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.CommandTimeout = 600;  // 대용량 WAV 삽입 대비 10분
            cmd.Parameters.AddWithValue("@refSig", refCollectSigId);
            var pWav = cmd.Parameters.Add("@wavBytes", System.Data.SqlDbType.VarBinary, -1);
            pWav.Value = wavBytes;
            cmd.Parameters.AddWithValue("@sr", sampleRate);
            cmd.Parameters.AddWithValue("@lenSec", lengthSec);
            cmd.Parameters.AddWithValue("@chngDt", DateTime.Now);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── 유틸 ──────────────────────────────────────────────

        // [NAIMS-ISSUE:CHAR-PADDING] 위 Truncate 와 같은 이유 — 정규화 후 자른다.
        private static string TruncateText(string s, int max)
        {
            var t = LegacyText.Normalize(s);
            return t.Length > max ? t.Substring(0, max) : t;
        }

        /// <summary>경로처럼 뒤쪽이 더 식별성 높은 문자열은 뒤에서 자른다.</summary>
        private static string TruncateRight(string s, int max)
        {
            var t = LegacyText.Normalize(s);
            return t.Length > max ? t.Substring(t.Length - max) : t;
        }

        private static string FormatBytesShort(long b)
        {
            if (b < 1024) return $"{b}";
            if (b < 1024L * 1024) return $"{b / 1024}K";
            if (b < 1024L * 1024 * 1024) return $"{b / 1024 / 1024}M";
            return $"{b / 1024 / 1024 / 1024}G";
        }
    }
}
