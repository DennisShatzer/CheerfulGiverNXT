using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data
{
    public static class SqlAppLock
    {
        public static async Task AcquireAsync(SqlConnection conn, string resource, CancellationToken ct)
        {
            await using var cmd = new SqlCommand("sp_getapplock", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.AddWithValue("@Resource", resource);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", 30000); // ms

            var returnParam = cmd.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
            returnParam.Direction = ParameterDirection.ReturnValue;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var rc = (int)returnParam.Value;
            if (rc < 0)
                throw new InvalidOperationException($"Failed to acquire SQL applock '{resource}'. Return code: {rc}");
        }

        public static async Task ReleaseAsync(SqlConnection conn, string resource, CancellationToken ct)
        {
            await using var cmd = new SqlCommand("sp_releaseapplock", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.AddWithValue("@Resource", resource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
