namespace DBImplant_N1toN2.DataAccess
{
    public static class LegacyDbConnectionFactory
    {
        public static ILegacyDbConnection Create(LegacyDbType dbType, string connectionString)
            => dbType switch
            {
                LegacyDbType.MsSql  => new MsSqlLegacyConnection(connectionString),
                LegacyDbType.Oracle => new OracleLegacyConnection(connectionString),
                _ => throw new System.ArgumentOutOfRangeException(nameof(dbType)),
            };
    }
}
