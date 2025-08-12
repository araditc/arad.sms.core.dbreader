using System.Data.Common;

namespace Arad.SMS.Core.DbReader.Factory;

public interface IDbConnectionFactory
{
    Task<DbConnection> CreateOpenConnectionAsync(CancellationToken token = default);

    DbCommand CreateCommand(string commandText, DbConnection connection, Dictionary<string, object>? parameters = null);

    Task<string> CreateArchiveTable(DateTime creationDate, CancellationToken token);
}