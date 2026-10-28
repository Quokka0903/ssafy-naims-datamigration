using System.Data;
using System.Data.Common;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 소스/타겟 DB에서 테이블 존재 여부, 레코드 수, 컬럼 스키마, 상위 N행 샘플을 읽는 헬퍼.
    /// MsSQL / Oracle 양쪽을 <see cref="DbProvider"/> 방언 분기로 처리한다.
    /// </summary>
    public static class TableInspector
    {
        public static async Task<long?> TryGetRowCountAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('*')) return null;
            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync(ct);
                if (!await TableExistsAsync(kind, mode, conn, tableName, ct)) return null;

                var quoted = DbProvider.QuoteIdent(kind, tableName);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = DbProvider.RowCountSql(kind, quoted);
                cmd.CommandTimeout = 60;
                var v = await cmd.ExecuteScalarAsync(ct);
                return v is null or DBNull ? null : Convert.ToInt64(v);
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "행 수 미확인(null)"으로 둔갑하는 것 방지
            catch { return null; }   // 실패=null 반환 계약은 호출부가 의존하므로 유지
        }

        public static async Task<bool> TableExistsAsync(
            DbProviderKind kind, DbConnectionMode mode, DbConnection conn, string tableName, CancellationToken ct = default)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = DbProvider.TableExistsSql(kind, mode);
            AddParam(cmd, DbProvider.ParamMarker(kind, mode, "t"), tableName);
            var r = await cmd.ExecuteScalarAsync(ct);
            return r is not (null or DBNull);
        }

        public static async Task<List<ColumnInfo>> GetColumnsAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName, CancellationToken ct = default)
        {
            var list = new List<ColumnInfo>();
            if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('*')) return list;

            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync(ct);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = DbProvider.ColumnsSql(kind, mode);
                AddParam(cmd, DbProvider.ParamMarker(kind, mode, "t"), tableName);

                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    list.Add(new ColumnInfo
                    {
                        Name      = r.GetString(0),
                        DataType  = r.GetString(1),
                        MaxLength = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2)),
                        // Oracle nullable 은 'Y'/'N', MsSQL 은 'YES'/'NO'
                        Nullable  = NormalizeNullable(r.IsDBNull(3) ? "" : r.GetValue(3).ToString() ?? ""),
                        Ordinal   = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4)),
                    });
                }
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "컬럼 0개"로 둔갑하는 것 방지
            catch { }   // 실패=빈 목록 반환 계약은 호출부가 의존하므로 유지
            return list;
        }

        /// <summary>
        /// 상위 N행 조회. blob/clob 등 대용량 컬럼은 제외해 그리드 부담을 줄인다.
        /// </summary>
        /// <param name="maxColumns">전시할 최대 컬럼 수. 0 이하면 제한 없음.</param>
        public static async Task<DataTable> GetSampleAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName,
            int top = 5, int maxColumns = 12, CancellationToken ct = default)
        {
            var dt = new DataTable();
            if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('*')) return dt;

            var cols = await GetColumnsAsync(kind, mode, connStr, tableName, ct);
            if (cols.Count == 0) return dt;

            IEnumerable<ColumnInfo> pick = cols.Where(c => !DbProvider.IsBinaryLike(kind, c.DataType));
            if (maxColumns > 0) pick = pick.Take(maxColumns);

            var visible = pick.Select(c => DbProvider.QuoteColumn(kind, c.Name)).ToList();
            if (visible.Count == 0) return dt;

            var sql = DbProvider.TopRowsSql(
                kind, DbProvider.QuoteIdent(kind, tableName), string.Join(", ", visible), top);

            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                using var r = await cmd.ExecuteReaderAsync(ct);
                dt.Load(r);
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "조회 오류" 테이블로 둔갑하는 것 방지
            catch (Exception ex)
            {
                // 조회 실패 사유를 호출부가 볼 수 있도록 한 행짜리 오류 테이블로 반환
                dt = new DataTable();
                dt.Columns.Add("조회 오류");
                dt.Rows.Add(ex.Message);
            }
            return dt;
        }

        /// <summary>
        /// 복호화 규칙이 적용된 상태에서 상위 N행을 조회하되, 암호화 컬럼은
        /// "컬럼 (암호문)"(RAW/암호문 앞 32바이트 헥스)과 "컬럼 (복호)"(복호화 결과) 두 열로
        /// 나란히 보여 준다. MsSQL 은 같은 연결에서 대칭키를 연 뒤 조회한다.
        /// </summary>
        public static async Task<DataTable> GetDecryptedSampleAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName,
            CryptoContext crypto, int top = 50, CancellationToken ct = default)
        {
            var dt = new DataTable();
            if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('*')) return dt;

            var cols = await GetColumnsAsync(kind, mode, connStr, tableName, ct);
            if (cols.Count == 0) return dt;

            var selects = new List<string>();
            foreach (var c in cols)
            {
                if (CryptoContext.IsEncrypted(kind, tableName, c.Name))
                {
                    // 전(前): 암호문/RAW 헥스, 후(後): 복호화 결과
                    selects.Add($"{CryptoContext.RawPreviewExpression(kind, c.Name)} AS "
                                + DbProvider.QuoteColumn(kind, c.Name + " (암호문)"));
                    selects.Add($"{crypto.DecryptExpressionOnly(kind, tableName, c)} AS "
                                + DbProvider.QuoteColumn(kind, c.Name + " (복호)"));
                }
                else if (!DbProvider.IsBinaryLike(kind, c.DataType))
                {
                    selects.Add(DbProvider.QuoteColumn(kind, c.Name));
                }
                // 암호화 아닌 바이너리/대용량 컬럼은 제외 (기존 동작과 동일)
            }
            if (selects.Count == 0) return dt;

            var sql = DbProvider.TopRowsSql(
                kind, DbProvider.QuoteIdent(kind, tableName), string.Join(", ", selects), top);

            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync(ct);
                await crypto.OpenKeyAsync(conn, ct);   // MsSQL 대칭키 개방 (Oracle 은 no-op)
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 120;
                using var r = await cmd.ExecuteReaderAsync(ct);
                dt.Load(r);
                await crypto.CloseKeyAsync(conn, ct);
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "조회 오류" 테이블로 둔갑하는 것 방지
            catch (Exception ex)
            {
                dt = new DataTable();
                dt.Columns.Add("조회 오류");
                dt.Rows.Add(ex.Message);
            }
            return dt;
        }

        /// <summary>
        /// ID 목록에 해당하는 행만 조회한다(이관 결과 좌우 대조용). 바이너리 컬럼은 제외하고,
        /// 암호화 컬럼은 <paramref name="crypto"/> 가 있으면 같은 이름으로 복호화해 담는다(없으면 숨김).
        /// 컬럼 순서는 <paramref name="cols"/> 순서를 따르고, 행 순서는 보장하지 않는다 —
        /// 호출부가 ID 순서로 재정렬해 좌우 행 인덱스를 맞춘다.
        /// </summary>
        // [찾기:레코드매칭]
        /// <summary>
        /// 지정한 컬럼만 상위 N행 조회한다(DB 간 데이터 비교용). 이관 세션 기록이 없을 때
        /// "양쪽을 통째로 읽어 키로 맞춰보는" 사후 검증 경로에 쓴다.
        /// 바이너리 컬럼은 제외하고, 암호화 컬럼은 crypto 가 있으면 복호화해 같은 이름으로 담는다.
        /// </summary>
        public static async Task<DataTable> GetTopRowsAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName,
            IReadOnlyList<ColumnInfo> cols, int top,
            CryptoContext? crypto = null, CancellationToken ct = default)
        {
            var dt = new DataTable();
            if (cols.Count == 0 || top <= 0) return dt;

            var selects = new List<string>();
            foreach (var c in cols)
            {
                if (CryptoContext.IsEncrypted(kind, tableName, c.Name))
                {
                    if (crypto != null)
                        selects.Add($"{crypto.DecryptExpressionOnly(kind, tableName, c)} AS "
                                    + DbProvider.QuoteColumn(kind, c.Name));
                }
                else if (!DbProvider.IsBinaryLike(kind, c.DataType))
                {
                    selects.Add(DbProvider.QuoteColumn(kind, c.Name));
                }
            }
            if (selects.Count == 0) return dt;

            await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
            await conn.OpenAsync(ct);
            if (crypto != null) await crypto.OpenKeyAsync(conn, ct);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = DbProvider.TopRowsSql(
                    kind, DbProvider.QuoteIdent(kind, tableName), string.Join(", ", selects), top);
                cmd.CommandTimeout = 180;
                using var r = await cmd.ExecuteReaderAsync(ct);
                dt.Load(r);
            }
            if (crypto != null) await crypto.CloseKeyAsync(conn, ct);
            return dt;
        }

        public static async Task<DataTable> GetRowsByIdsAsync(
            DbProviderKind kind, DbConnectionMode mode, string connStr, string tableName,
            IReadOnlyList<ColumnInfo> cols, string idColumn, IReadOnlyList<long> ids,
            CryptoContext? crypto = null, CancellationToken ct = default)
        {
            var dt = new DataTable();
            if (ids.Count == 0 || cols.Count == 0) return dt;

            var selects = new List<string>();
            foreach (var c in cols)
            {
                if (CryptoContext.IsEncrypted(kind, tableName, c.Name))
                {
                    // 복호화 가능하면 같은 이름으로 복호값을, 불가하면 숨긴다(기존 샘플 조회와 동일 정책)
                    if (crypto != null)
                        selects.Add($"{crypto.DecryptExpressionOnly(kind, tableName, c)} AS "
                                    + DbProvider.QuoteColumn(kind, c.Name));
                }
                else if (!DbProvider.IsBinaryLike(kind, c.DataType))
                {
                    selects.Add(DbProvider.QuoteColumn(kind, c.Name));
                }
            }
            if (selects.Count == 0) return dt;

            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync(ct);
                if (crypto != null) await crypto.OpenKeyAsync(conn, ct);

                // Oracle IN 목록 1000개 제한 대비 청크 조회
                for (int i = 0; i < ids.Count; i += 900)
                {
                    var chunk = ids.Skip(i).Take(900);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        $"SELECT {string.Join(", ", selects)} FROM {DbProvider.QuoteIdent(kind, tableName)} " +
                        $"WHERE {DbProvider.QuoteColumn(kind, idColumn)} IN ({string.Join(",", chunk)})";
                    cmd.CommandTimeout = 120;
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    if (dt.Columns.Count == 0) dt.Load(r);
                    else
                    {
                        var part = new DataTable();
                        part.Load(r);
                        foreach (DataRow row in part.Rows) dt.ImportRow(row);
                    }
                }
                if (crypto != null) await crypto.CloseKeyAsync(conn, ct);
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "조회 오류" 테이블로 둔갑하는 것 방지
            catch (Exception ex)
            {
                dt = new DataTable();
                dt.Columns.Add("조회 오류");
                dt.Rows.Add(ex.Message);
            }
            return dt;
        }

        private static void AddParam(DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        private static string NormalizeNullable(string raw) => raw.Trim().ToUpperInvariant() switch
        {
            "Y" or "YES" => "YES",
            "N" or "NO"  => "NO",
            _ => raw
        };
    }
}
