using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth.Api.Migration;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Model.Dto;
using Newtonsoft.Json;

try
{
    if (args.Length == 5 && args[0] == "rollback" && args[4] == "--server-stopped")
    {
        var current = File.ReadAllBytes(args[1]);
        var backup = File.ReadAllBytes(args[2]);
        var receipt = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(args[3]))!;
        if (Hash(current) != receipt.After || Hash(backup) != receipt.Before)
            throw new InvalidOperationException("Rollback refused: configuration changed after migration or backup does not match. Restore manually after reviewing account policies.");
        ReplaceChecked(args[1], current, backup);
        Console.WriteLine("Restored the exact pre-migration configuration. Backup and receipt retained.");
        return 0;
    }

    if (args.Length < 6 || !(args[0] == "preview" && args.Length == 6 || args[0] == "apply" && args.Length == 7 && args[6] == "--server-stopped"))
    {
        Console.Error.WriteLine("preview CONFIG USERS_JSON oid|saml PROVIDER PLAN_JSON\napply CONFIG USERS_JSON oid|saml PROVIDER PLAN_JSON --server-stopped\nrollback CONFIG BACKUP RECEIPT --server-stopped");
        return 2;
    }

    var bytes = File.ReadAllBytes(args[1]);
    var serializer = new XmlSerializer(typeof(PluginConfiguration));
    // Refuse unknown fields rather than silently dropping data from a newer plugin.
    serializer.UnknownElement += (_, _) => throw new InvalidOperationException("Unknown configuration field; use a matching tool/plugin version.");
    using var input = new MemoryStream(bytes);
    using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 16 * 1024 * 1024 });
    var config = (PluginConfiguration)serializer.Deserialize(reader)!;
    var users = JsonConvert.DeserializeObject<UserDto[]>(File.ReadAllText(args[2]))!.ToDictionary(user => user.Id);
    UserDto? GetUser(Guid id) => users.GetValueOrDefault(id);
    if (args[0] == "preview")
    {
        WritePrivate(args[5], System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
            ProviderModelMigration.Preview(config, args[3], args[4], GetUser), Newtonsoft.Json.Formatting.Indented)));
        Console.WriteLine("Preview written. Confirm every Subject and explicit ManualFolders entry; CurrentFolders is reference only.");
        return 0;
    }

    var plan = JsonConvert.DeserializeObject<MigrationPreview>(File.ReadAllText(args[5]));
    var migrated = ProviderModelMigration.Apply(config, args[3], args[4], plan, GetUser);
    using var output = new MemoryStream();
    serializer.Serialize(output, migrated);
    var updated = output.ToArray();
    var suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ");
    var backupPath = args[1] + "." + suffix + ".before-migration";
    var receiptPath = backupPath + ".receipt.json";
    WritePrivate(backupPath, bytes);
    WritePrivate(receiptPath, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Receipt(Hash(bytes), Hash(updated)), Newtonsoft.Json.Formatting.Indented)));
    ReplaceChecked(args[1], bytes, updated);
    Console.WriteLine($"Applied. Backup: {backupPath}\nReceipt: {receiptPath}\nRollback is available only before any configuration change or migrated login.");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or XmlException)
{
    // Do not echo exception chains: XML/JSON parser errors can contain credentials.
    Console.Error.WriteLine("Migration failed; configuration was not replaced unless success was reported. Check file access, tool/plugin versions, complete reviewed mappings and a fresh preview. " + ex.GetType().Name);
    return 1;
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static void WritePrivate(string path, byte[] bytes)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    using var stream = new FileStream(path, options);
    stream.Write(bytes);
    stream.Flush(true);
}

static void ReplaceChecked(string path, byte[] original, byte[] updated)
{
    if (new FileInfo(path).LinkTarget != null) throw new InvalidOperationException("Use the actual configuration path, not a symbolic link.");
    var temp = path + ".migration-" + Guid.NewGuid().ToString("N");
    try
    {
        WritePrivate(temp, updated);
        if (!File.ReadAllBytes(path).SequenceEqual(original)) throw new InvalidOperationException("Configuration changed during migration.");
        File.Move(temp, path, overwrite: true);
    }
    finally { if (File.Exists(temp)) File.Delete(temp); }
}

internal record Receipt(string Before, string After);
