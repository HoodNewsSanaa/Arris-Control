using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArrisControl;
internal static class AcquisitionStore
{
    public static readonly string Folder=Path.Combine(CredentialStore.UserDirectory,"Acquired");
    static readonly byte[] Entropy=Encoding.UTF8.GetBytes("ArrisControl.AcquiredCredential.v1");
    public static string Save(AcquiredCredential acquired,string? directory=null)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(acquired);byte[]? check=null;string? temporary=null;
        try
        {
            var cipher=CredentialProtection.Protect(plain,Entropy);check=CredentialProtection.Unprotect(cipher,Entropy);
            if(!CryptographicOperations.FixedTimeEquals(plain,check))throw new RouterException("Credential protection could not be verified.");
            var folder=directory??Folder;Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,$"account-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");temporary=file+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new{Format="ARRIS-AcquiredCredential",Version=1,CreatedAtUTC=DateTimeOffset.UtcNow,Protection=CredentialProtection.Description,LocalAuthenticationVerified=false,CloudTokensSaved=false,Ciphertext=Convert.ToBase64String(cipher)}));
            File.Move(temporary,file);temporary=null;return file;
        }
        catch(CryptographicException){throw new RouterException("This device could not protect the acquired credential. No plaintext file was saved.");}
        finally{CryptographicOperations.ZeroMemory(plain);if(check is not null)CryptographicOperations.ZeroMemory(check);if(temporary is not null && File.Exists(temporary))File.Delete(temporary);}
    }
    public static AcquiredCredential Load(string file,bool preserve=true)
    {
        byte[]? plain=null;
        try
        {
            if(new FileInfo(file).Length>262144)throw new RouterException("Acquisition record is too large.");
            using var record=JsonDocument.Parse(File.ReadAllBytes(file));
            if(record.RootElement.GetProperty("Format").GetString()!="ARRIS-AcquiredCredential" || record.RootElement.GetProperty("Version").GetInt32()!=1)throw new RouterException("Select a protected acquisition record from the Acquired folder.");
            var cipher=Convert.FromBase64String(record.RootElement.GetProperty("Ciphertext").GetString()!);plain=CredentialProtection.Unprotect(cipher,Entropy);
            var acquired=JsonSerializer.Deserialize<AcquiredCredential>(plain)??throw new RouterException("Empty acquisition record.");
            if(acquired.Credential.UserName.Length==0 || acquired.Credential.Password.Length==0)throw new RouterException("Incomplete acquisition record.");
            if(preserve && !Path.GetFullPath(file).StartsWith(Path.GetFullPath(Folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Save(acquired);
            return acquired;
        }
        catch(Exception ex) when(ex is CryptographicException or JsonException or FormatException or KeyNotFoundException or IOException)
        {throw new RouterException("This device could not load this acquisition record for the current user. No network request was sent.");}
        finally{if(plain is not null)CryptographicOperations.ZeroMemory(plain);}
    }
}
