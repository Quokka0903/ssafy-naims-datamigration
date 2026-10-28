namespace DBImplant_N1toN2.DataAccess
{
    public enum LegacyDbType
    {
        MsSql,
        Oracle,
    }

    /// <summary>
    /// 레거시 DB 접속 방식. 폐쇄망 현행은 ODBC(윈도우 ODBC 드라이버/DSN 경유)이고,
    /// 다이렉트는 벤더 관리형 드라이버(OracleConnection / SqlConnection)로 직접 붙는다.
    /// </summary>
    public enum LegacyConnectionMode
    {
        /// <summary>Windows ODBC 드라이버 관리자 경유(DSN 또는 DSN-less). 폐쇄망 기본값.</summary>
        Odbc,
        /// <summary>벤더 관리형 드라이버로 직접 접속.</summary>
        Direct,
    }
}
