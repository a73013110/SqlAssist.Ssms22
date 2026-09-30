using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 稽核直連的伺服器：由 <c>tools/Set-CompletionAuditConnection.ps1</c> 寫在版控外，密碼以 DPAPI 加密。
/// </summary>
/// <remarks>
/// 密碼解開後只放進 <see cref="SecureString"/> 交給 <see cref="SqlCredential"/>，不進連線字串：
/// 連線字串會出現在例外、紀錄與快取鍵裡，密碼不在上面就不可能跟著出去。
/// 登入名稱留空是 Windows 驗證；埠是 0 時不指定，給具名執行個體。
/// </remarks>
internal sealed class AuditConnection
{
    private readonly string? _encryptedPassword;

    private AuditConnection(
        string server,
        int port,
        IReadOnlyList<string> databases,
        string? login,
        string? encryptedPassword,
        bool trustServerCertificate)
    {
        Server = server;
        Port = port;
        Databases = databases;
        Login = login;
        _encryptedPassword = encryptedPassword;
        TrustServerCertificate = trustServerCertificate;
    }

    /// <summary>預設的設定檔位置；與 <c>Set-CompletionAuditConnection.ps1</c> 同一個。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAssist.Ssms22", "CompletionAudit", "connection.json");

    public string Server { get; }

    public int Port { get; }

    public IReadOnlyList<string> Databases { get; }

    /// <summary>SQL 驗證的登入名稱；null 是 Windows 驗證。</summary>
    public string? Login { get; }

    public bool TrustServerCertificate { get; }

    /// <summary>給紀錄看的：伺服器、埠與登入名稱，沒有密碼。</summary>
    public string Describe() => $"{DataSource}（{Login ?? "Windows 驗證"}）";

    private string DataSource => Port > 0 ? $"{Server},{Port.ToString(CultureInfo.InvariantCulture)}" : Server;

    /// <summary>讀設定檔；不存在時回傳 null，格式不對時擲出。</summary>
    public static AuditConnection? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var root = AuditJson.Parse(File.ReadAllText(path));
        var login = root.String("login") is { Length: > 0 } name ? name : null;
        var databases = new List<string>();

        foreach (var database in root.Array("databases"))
        {
            if (database is string { Length: > 0 } databaseName)
            {
                databases.Add(databaseName);
            }
        }

        return new AuditConnection(
            Required(root, "server", path),
            root.Int("port", 1433),
            databases,
            login,
            login is null ? null : Required(root, "password", path),
            root.Bool("trustServerCertificate"));
    }

    /// <summary>不含認證的連線字串；<paramref name="database"/> 為 null 時不指定資料庫。</summary>
    public string ConnectionString(string? database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = DataSource,
            IntegratedSecurity = Login is null,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = TrustServerCertificate,
            ApplicationName = "SqlAssist CompletionAudit",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = 15,
        };

        if (!string.IsNullOrEmpty(database))
        {
            builder.InitialCatalog = database;
        }

        return builder.ConnectionString;
    }

    /// <summary>登入名稱與解開的密碼；每次新建，用完由連線持有。Windows 驗證時是 null。</summary>
    public SqlCredential? Credential()
    {
        if (Login is null || _encryptedPassword is null)
        {
            return null;
        }

        var bytes = ProtectedData.Unprotect(FromHex(_encryptedPassword), null, DataProtectionScope.CurrentUser);

        try
        {
            // ConvertFrom-SecureString 存的是 UTF-16LE。
            var password = new SecureString();

            for (var index = 0; index + 1 < bytes.Length; index += 2)
            {
                password.AppendChar((char)(bytes[index] | bytes[index + 1] << 8));
            }

            password.MakeReadOnly();
            return new SqlCredential(Login, password);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    /// <summary>開一條連到 <paramref name="database"/> 的連線。</summary>
    public SqlConnection Open(string? database)
    {
        var connection = new SqlConnection(ConnectionString(database), Credential());

        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string Required(Dictionary<string, object> root, string name, string path) =>
        root.String(name) is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"連線設定缺少 {name}：{path}");

    private static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new InvalidDataException("連線設定的密碼不是 ConvertFrom-SecureString 的格式。");
        }

        var bytes = new byte[hex.Length / 2];

        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}
