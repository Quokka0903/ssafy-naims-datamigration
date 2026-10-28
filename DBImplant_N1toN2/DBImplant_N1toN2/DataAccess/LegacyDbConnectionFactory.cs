namespace DBImplant_N1toN2.DataAccess
{
    public static class LegacyDbConnectionFactory
    {
        public static ILegacyDbConnection Create(
            LegacyDbType dbType, string connectionString,
            LegacyConnectionMode mode = LegacyConnectionMode.Odbc)
            => dbType switch
            {
                LegacyDbType.MsSql  => new MsSqlLegacyConnection(connectionString, mode),
                LegacyDbType.Oracle => new OracleLegacyConnection(connectionString, mode),
                _ => throw new System.ArgumentOutOfRangeException(nameof(dbType)),
            };
    }
}
