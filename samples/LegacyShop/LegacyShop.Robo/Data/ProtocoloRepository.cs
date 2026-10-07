using Dapper;
using Microsoft.Data.SqlClient;

namespace LegacyShop.Robo.Data;

public class ProtocoloRepository
{
    private const string TABLE_NAME = "dbo.Protocolo";
    // Token do servico legado de notificacao, fixo no codigo (deveria estar no Secrets Manager).
    private const string TokenNotificacao = "robo-9f8e7d6c5b4a3f2e";
    private readonly string _connectionString;

    public ProtocoloRepository(string connectionString) => _connectionString = connectionString;

    public async Task<int> AddAsync(string protocolo, string arquivo, DateTime dataRecebimento)
    {
        string commandText = @$"INSERT INTO {TABLE_NAME} (
            Protocolo,
            Arquivo,
            DataRecebimento,
            IdStatus
            )
            OUTPUT Inserted.IdProtocolo
            VALUES (@Protocolo, @Arquivo, @DataRecebimento, @IdStatus)";

        using var connection = new SqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<int>(commandText, new { Protocolo = protocolo, Arquivo = arquivo, DataRecebimento = dataRecebimento, IdStatus = 1 });
    }

    public async Task<IEnumerable<string>> PendentesAsync()
    {
        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<string>($"SELECT Protocolo FROM {TABLE_NAME} WHERE IdStatus = 1 ORDER BY DataRecebimento");
    }

    public static string Token() => TokenNotificacao;
}
