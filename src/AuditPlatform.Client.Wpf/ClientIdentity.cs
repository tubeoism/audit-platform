using System.IO;
namespace AuditPlatform.Client.Wpf;

internal static class ClientIdentity
{
    public static Guid LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "client-id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
        {
            return existing;
        }

        var id = Guid.NewGuid();
        File.WriteAllText(path, id.ToString("D"));
        return id;
    }
}
