using System.Net;
using MySql.Data.MySqlClient;

namespace AllenBuilding.Infrastructure.Persistence;

public static class MySqlConnectionPolicy
{
    public static string Validate(string connection, bool local, bool migrations = false)
    {
        MySqlConnectionStringBuilder options;
        try { options = new MySqlConnectionStringBuilder(connection); }
        catch (Exception) { throw new InvalidOperationException("The MySQL connection configuration is invalid."); }
        if (local) return options.ConnectionString;
        var supplied = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = options.ConnectionString };
        var explicitPool = supplied.Keys.Cast<string>().Any(x => x.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() is "maximumpoolsize" or "maxpoolsize");
        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.Database) || string.IsNullOrWhiteSpace(options.UserID) ||
            string.IsNullOrEmpty(options.Password) || !options.Pooling || options.MinimumPoolSize > Math.Min(options.MaximumPoolSize, 10) ||
            (explicitPool && options.MaximumPoolSize is < 1 or > 10) ||
            options.AllowLoadLocalInfile || options.PersistSecurityInfo)
            throw new InvalidOperationException("Production MySQL requires dedicated credentials, a pool of at most ten connections and restricted connection features.");
        var loopback = options.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(options.Server, out var address) && IPAddress.IsLoopback(address));
        var socket = options.ConnectionProtocol == MySqlConnectionProtocol.UnixSocket && Path.IsPathFullyQualified(options.Server);
        if (!loopback && !socket && options.SslMode != MySqlSslMode.VerifyFull)
            throw new InvalidOperationException("Cross-host MySQL connections require verified hostname and certificate trust with SslMode=VerifyFull.");
        options.MaximumPoolSize = explicitPool ? options.MaximumPoolSize : 10;
        if (migrations)
        {
            if (options.MinimumPoolSize > 2) throw new InvalidOperationException("The isolated migration process permits at most two connections.");
            options.MaximumPoolSize = Math.Min(options.MaximumPoolSize, 2);
        }
        options.ConnectionTimeout = Math.Min(options.ConnectionTimeout, 5);
        options.DefaultCommandTimeout = Math.Min(options.DefaultCommandTimeout, 15);
        if (options.ConnectionTimeout == 0 || options.DefaultCommandTimeout == 0)
            throw new InvalidOperationException("Production MySQL requires bounded connection and command timeouts.");
        // Connector/NET 26.7 migrations fail under Linux with reset enabled.
        // The exclusive short-lived migrator owns its session; ordinary pooled application sessions still reset.
        options.ConnectionReset = !migrations;
        return options.ConnectionString;
    }
}
