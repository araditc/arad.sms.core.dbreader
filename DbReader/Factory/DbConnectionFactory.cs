using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;

using MySqlConnector;

using Oracle.ManagedDataAccess.Client;

using Polly;
using Polly.Retry;

using Serilog;

namespace Arad.SMS.Core.DbReader.Factory;

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly AsyncRetryPolicy _retryPolicy;

    public DbConnectionFactory()
    {
        _retryPolicy = Policy.Handle<DbException>().
                              Or<TimeoutException>().
                              WaitAndRetryAsync(3,
                                                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                                                (exception, timeSpan, retryCount, _) =>
                                                {
                                                    Log.Warning("Retry {RetryCount} due to {Exception}. Waiting {TimeSpan}",
                                                                retryCount,
                                                                exception.Message,
                                                                timeSpan);
                                                });
    }

    public async Task<DbConnection> CreateOpenConnectionAsync(CancellationToken token = default)
    {
        DbConnection connection = CreateConnection();

        await _retryPolicy.ExecuteAsync(async () =>
                                        {
                                            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10)); // Timeout هوشمند
                                            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);

                                            await connection.OpenAsync(linkedCts.Token);
                                        });

        return connection;
    }

    public DbCommand CreateCommand(string commandText, DbConnection connection, Dictionary<string, object>? parameters = null)
    {
        DbCommand command = RuntimeSettings.DbProvider switch
                            {
                                "SQL" => new SqlCommand(commandText, (SqlConnection)connection),
                                "MySQL" => new MySqlCommand(commandText, (MySqlConnection)connection),
                                "Oracle" => new OracleCommand(commandText, (OracleConnection)connection),
                                _ => throw new NotSupportedException($"Unsupported provider: {RuntimeSettings.DbProvider}")
                            };
        command.CommandTimeout = 120;
        command.CommandType = CommandType.Text;

        if (parameters != null)
        {
            command.CommandType = CommandType.StoredProcedure;
            foreach (KeyValuePair<string, object> param in parameters)
            {
                DbParameter dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Key;
                dbParam.Value = param.Value;
                command.Parameters.Add(dbParam);
            }
        }

        return command;
    }

    private DbConnection CreateConnection() =>
        RuntimeSettings.DbProvider switch
        {
            "SQL" => new SqlConnection(RuntimeSettings.ConnectionString),
            "MySQL" => new MySqlConnection(RuntimeSettings.ConnectionString),
            "Oracle" => new OracleConnection(RuntimeSettings.ConnectionString),
            _ => throw new NotSupportedException($"Unsupported provider: {RuntimeSettings.DbProvider}")
        };

    public async Task<string> CreateArchiveTable(DateTime creationDate, CancellationToken token)
    {
        PersianCalendar persianCalendar = new();
        string archiveTableName = $"{RuntimeSettings.OutboxTableName}_Archive_{persianCalendar.GetYear(creationDate)}{persianCalendar.GetMonth(creationDate).ToString().PadLeft(2, '0')}";
        
        if (await ExistArchiveTable(archiveTableName, token))
        {
            return archiveTableName;
        }

        return await (RuntimeSettings.DbProvider switch
                      {
                          "SQL" => CreateFullArchiveTableSQLServerAsync(archiveTableName, token),
                          "MySQL" => CreateArchiveTableMySQLAsync(archiveTableName, token),
                          //"Oracle" => new OracleConnection(_connectionString),
                          _ => throw new NotSupportedException($"Unsupported provider: {RuntimeSettings.DbProvider}")
                      });
    }

    private async Task<bool> ExistArchiveTable(string archiveTableName, CancellationToken token)
    {
        await using DbConnection connection = await CreateOpenConnectionAsync(token);
        try
        {
            string query = RuntimeSettings.DbProvider switch
                           {
                               "SQL" => $"SELECT COUNT(*) FROM sys.tables WHERE name = '{archiveTableName}';",
                               "MySQL" => $" SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{archiveTableName}'",
                               //"Oracle" => new OracleConnection(_connectionString),
                               _ => throw new NotSupportedException($"Unsupported provider: {RuntimeSettings.DbProvider}")
                           };

            DbCommand dbCommand=  CreateCommand(query, connection);
            int count = Convert.ToInt32(await dbCommand.ExecuteScalarAsync(token));
            return count > 0;
        }
        catch (Exception ex)
        {
            Log.Error("Error: {ExMessage}", ex.Message);
        }

        return false;
    }

    private async Task<string> CreateArchiveTableMySQLAsync(string archiveTableName, CancellationToken token)
    {
        try
        {
            string query = $"SHOW CREATE TABLE `{RuntimeSettings.OutboxTableName}`;";
            string createTableScript = "";

            MySqlConnection connection = new(RuntimeSettings.ConnectionString);

            await using (MySqlCommand command = new(query, connection))
            {
                await using (MySqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    if (await reader.ReadAsync(token))
                    {
                        createTableScript = reader.GetString(1); // ستون دوم حاوی اسکریپت است
                    }
                }
            }

            if (!string.IsNullOrEmpty(createTableScript))
            {
                // جایگزینی نام جدول قدیمی با نام جدید
                createTableScript = Regex.Replace(createTableScript, @$"\b{RuntimeSettings.OutboxTableName}\b", archiveTableName, RegexOptions.IgnoreCase);

                // اجرای اسکریپت برای ایجاد جدول جدید
                await using MySqlCommand createCommand = new(createTableScript, connection);
                await createCommand.ExecuteNonQueryAsync(token);
                Log.Error("Table '{ArchiveTableName}' created successfully!", archiveTableName);
            }
            else
            {
                Log.Error("Failed to retrieve table script.");
            }

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Error: {ExMessage}", ex.Message);
        }

        return archiveTableName;
    }
    
    private async Task<string> CreateFullArchiveTableSQLServerAsync(string archiveTableName, CancellationToken token)
    {
        try
        {
            await using SqlConnection connection = new(RuntimeSettings.ConnectionString);
            await connection.OpenAsync(token);

            // -------- 1. ستون‌ها --------
            string columnQuery = @"
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH,
                   IS_NULLABLE, COLUMN_DEFAULT
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @TableName
            ORDER BY ORDINAL_POSITION";
            List<string> columns = [];
            await using (SqlCommand cmd = new(columnQuery, connection))
            {
                cmd.Parameters.AddWithValue("@TableName", RuntimeSettings.OutboxTableName);
                await using SqlDataReader? reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    string name = reader.GetString(0);
                    string type = reader.GetString(1);
                    object maxLenObj = (reader.IsDBNull(2) ? null : reader.GetValue(2))!;
                    string isNullable = reader.GetString(3);

                    string lengthPart = "";
                    if (maxLenObj is int maxLen &&
                        type is "varchar" or "nvarchar" or "char" or "nchar")
                    {
                        lengthPart = $"({(maxLen == -1 ? "MAX" : maxLen)})";
                    }

                    string nullablePart = isNullable == "YES" ? "NULL" : "NOT NULL";

                    columns.Add($"[{name}] {type}{lengthPart} {nullablePart}");
                }
            }

            // -------- 2. Primary Key --------
            string pkQuery = @"
            SELECT k.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                 ON t.CONSTRAINT_NAME = k.CONSTRAINT_NAME
            WHERE t.TABLE_NAME = @TableName AND t.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY k.ORDINAL_POSITION";

            List<string> pkColumns = [];
            await using (SqlCommand cmd = new(pkQuery, connection))
            {
                cmd.Parameters.AddWithValue("@TableName", RuntimeSettings.OutboxTableName);
                await using SqlDataReader? reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    pkColumns.Add($"[{reader.GetString(0)}]");
                }
            }

            // -------- 3. ایجاد CREATE TABLE --------
            StringBuilder createScript = new();
            createScript.AppendLine($"CREATE TABLE [{archiveTableName}] (");
            createScript.AppendLine(string.Join(",\n", columns));
            if (pkColumns.Any())
            {
                createScript.AppendLine($", CONSTRAINT [PK_{archiveTableName}] PRIMARY KEY ({string.Join(", ", pkColumns)})");
            }

            createScript.AppendLine(");");

            // -------- 4. ایندکس‌ها --------
            string indexQuery = @"
            SELECT i.name AS IndexName, i.is_unique, c.name AS ColumnName
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            WHERE t.name = @TableName AND i.is_primary_key = 0
            ORDER BY i.name, ic.key_ordinal";

            Dictionary<string, (bool IsUnique, List<string> Columns)> indexes = new();
            await using (SqlCommand cmd = new(indexQuery, connection))
            {
                cmd.Parameters.AddWithValue("@TableName", RuntimeSettings.OutboxTableName);
                await using SqlDataReader? reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    string idxName = reader.GetString(0);
                    bool isUnique = reader.GetBoolean(1);
                    string colName = reader.GetString(2);

                    if (!indexes.ContainsKey(idxName))
                    {
                        indexes[idxName] = (isUnique, []);
                    }

                    indexes[idxName].Columns.Add($"[{colName}]");
                }
            }

            foreach (KeyValuePair<string, (bool IsUnique, List<string> Columns)> idx in indexes)
            {
                string uniqueStr = idx.Value.IsUnique ? "UNIQUE" : "";
                createScript.AppendLine($"CREATE {uniqueStr} INDEX [{idx.Key}_{archiveTableName}] ON [{archiveTableName}] ({string.Join(", ", idx.Value.Columns)});");
            }

            // -------- 5. Foreign Keys --------
            string fkQuery = @"
            SELECT fk.name AS FKName,
                   tp.name AS ParentTable,
                   cp.name AS ParentColumn,
                   tr.name AS RefTable,
                   cr.name AS RefColumn
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            INNER JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
            INNER JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
            INNER JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
            INNER JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
            WHERE tp.name = @TableName";

            await using (SqlCommand cmd = new(fkQuery, connection))
            {
                cmd.Parameters.AddWithValue("@TableName", RuntimeSettings.OutboxTableName);
                await using SqlDataReader? reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    string fkName = reader.GetString(0);
                    string refTable = reader.GetString(3);
                    string parentCol = reader.GetString(2);
                    string refCol = reader.GetString(4);

                    createScript.AppendLine($"ALTER TABLE [{archiveTableName}] ADD CONSTRAINT [{fkName}_{archiveTableName}] FOREIGN KEY ([{parentCol}]) REFERENCES [{refTable}] ([{refCol}]);");
                }
            }

            // -------- 6. اجرای اسکریپت --------
            await using (SqlCommand cmd = new(createScript.ToString(), connection))
            {
                await cmd.ExecuteNonQueryAsync(token);
            }

            Log.Information("Table '{ArchiveTableName}' created successfully with full schema!", archiveTableName);
            return archiveTableName;
        }
        catch (Exception ex)
        {
            Log.Error("Error creating full archive table: {Message}", ex.Message);
            return string.Empty;
        }
    }
}