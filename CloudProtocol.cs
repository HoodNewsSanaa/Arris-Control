using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal sealed record CloudConfiguration(string Pool,string ClientId,string ClientSecret,string AccountBase)
{
    public override string ToString()=>"ARRIS MEGHA cloud protocol";
    public void Validate()
    {
        if(Pool!="us-east-1_TQzd61rzQ" || ClientId!="7kvme7eu7mpiihfc4pai87g5k2" ||
            AccountBase.TrimEnd('/')!="https://api1.surfboardconnect.com" || !Regex.IsMatch(ClientSecret,@"^[A-Za-z0-9+/=_-]{8,256}$"))
            throw new RouterException("This reference does not contain the reviewed ARRIS MEGHA configuration. Use SURFboard Central 4.3.5.");
    }
}

internal static class CloudProtocol
{
    static readonly string Folder=Path.Combine(CredentialStore.UserDirectory,"Protocol");
    static readonly byte[] Entropy=Encoding.UTF8.GetBytes("ArrisControl.CloudProtocol.v1");
    static readonly string[] Names=["AWS_USER_POOL_ID_MEGHA","AWS_USER_CLIENT_ID_MEGHA","AWS_USER_CLIENT_SECRET_MEGHA","AWS_USER_ECO_URL_MEGHA"];
    public static CloudConfiguration FromConstants(IReadOnlyDictionary<string,string> values)
    {
        if(Names.Any(n=>!values.ContainsKey(n)))throw new RouterException("The reference is missing ARRIS cloud protocol constants.");
        var configuration=new CloudConfiguration(values[Names[0]],values[Names[1]],values[Names[2]],values[Names[3]]);configuration.Validate();return configuration;
    }
    public static CloudConfiguration ReadReferences(IEnumerable<string> paths)
    {
        foreach(var path in paths.Where(File.Exists).Distinct())
        {
            try { return ReadReference(path); }
            catch(RouterException) { /* BuildConfig can live in a split APK rather than the base APK. */ }
            catch(UnauthorizedAccessException) { }
        }
        throw new RouterException("The installed APK/splits do not expose the supported ARRIS configuration. Import your own SURFboard Central 4.3.5 APK/XAPK or BuildConfig.java. No app was installed or executed.");
    }
    public static CloudConfiguration ReadReference(string path)
    {
        try
        {
            if(new FileInfo(path).Length>300*1024*1024)throw new RouterException("The reference file exceeds the size limit.");
            if(Path.GetExtension(path).Equals(".java",StringComparison.OrdinalIgnoreCase))
            {
                var text=File.ReadAllText(path);var values=new Dictionary<string,string>();
                foreach(var name in Names){var match=Regex.Match(text,@"\b"+name+"\\s*=\\s*\"([^\"\\r\\n]*)\";");if(match.Success)values[name]=match.Groups[1].Value;}
                return FromConstants(values);
            }
            using var file=File.OpenRead(path);using var zip=new ZipArchive(file,ZipArchiveMode.Read);
            var configuration=ReadApk(zip);
            if(configuration is not null)return configuration;
            foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".apk",StringComparison.OrdinalIgnoreCase)).OrderByDescending(e=>e.Length).Take(8))
            {
                using var input=entry.Open();using var memory=CopyBounded(input,entry.Length,128*1024*1024);using var apk=new ZipArchive(memory,ZipArchiveMode.Read);
                configuration=ReadApk(apk);if(configuration is not null)return configuration;
            }
            throw new RouterException("The APK/XAPK did not contain the expected SURFboard Central BuildConfig.");
        }
        catch(RouterException){throw;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {throw new RouterException("Could not read the static protocol reference. Nothing was installed or executed.");}
    }
    static CloudConfiguration? ReadApk(ZipArchive apk)
    {
        foreach(var entry in apk.Entries.Where(e=>Regex.IsMatch(e.FullName,@"^classes(?:\d+)?\.dex$")).Take(20))
        {
            using var input=entry.Open();using var memory=CopyBounded(input,entry.Length,64*1024*1024);
            var values=new DexConstants(memory.ToArray()).ReadBuildConfig();if(values is not null)return FromConstants(values);
        }
        return null;
    }
    static MemoryStream CopyBounded(Stream input,long advertised,long maximum)
    {
        if(advertised<0 || advertised>maximum)throw new RouterException("An archive member exceeds the static-analysis size limit.");
        var memory=new MemoryStream();var buffer=new byte[65536];int count;
        try{while((count=input.Read(buffer))>0){if(memory.Length+count>maximum)throw new RouterException("Archive member exceeded its limit.");memory.Write(buffer,0,count);}memory.Position=0;return memory;}
        catch{memory.Dispose();throw;}
    }
    public static CloudConfiguration? LoadSaved()
    {
        if(!Directory.Exists(Folder))return null;
        var file=Directory.EnumerateFiles(Folder,"protocol-*.json").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).FirstOrDefault();
        if(file is null)return null;
        byte[]? plain=null;
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllBytes(file));var cipher=Convert.FromBase64String(json.RootElement.GetProperty("Ciphertext").GetString()!);
            plain=CredentialProtection.Unprotect(cipher,Entropy);
            var configuration=JsonSerializer.Deserialize<CloudConfiguration>(plain)??throw new RouterException("Invalid protected protocol configuration.");configuration.Validate();return configuration;
        }
        catch(Exception ex) when(ex is CryptographicException or JsonException or FormatException or IOException)
        {throw new RouterException("This device could not load the saved protocol reference. Select the APK/XAPK reference again.");}
        finally{if(plain is not null)CryptographicOperations.ZeroMemory(plain);}
    }
    public static void Save(CloudConfiguration configuration)
    {
        configuration.Validate();var plain=JsonSerializer.SerializeToUtf8Bytes(configuration);byte[]? check=null;
        try
        {
            var cipher=CredentialProtection.Protect(plain,Entropy);check=CredentialProtection.Unprotect(cipher,Entropy);
            if(!CryptographicOperations.FixedTimeEquals(plain,check))throw new RouterException("Protocol protection round-trip failed.");
            Directory.CreateDirectory(Folder);var file=Path.Combine(Folder,$"protocol-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
            File.WriteAllText(file,JsonSerializer.Serialize(new{Format="ARRIS-CloudProtocol",Version=1,Protection=CredentialProtection.Description,Ciphertext=Convert.ToBase64String(cipher)}));
        }
        catch(CryptographicException){throw new RouterException("This device could not protect the protocol reference. No plaintext fallback is used.");}
        finally{CryptographicOperations.ZeroMemory(plain);if(check is not null)CryptographicOperations.ZeroMemory(check);}
    }
    public static CloudConfiguration? Discover()
    {
        var saved=LoadSaved();if(saved is not null)return saved;
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        for(var i=0;i<8 && dir is not null;i++,dir=dir.Parent)
        {
            var path=Path.Combine(dir.FullName,"decompiled","sources","com","arris","sbc","BuildConfig.java");
            if(File.Exists(path)){var result=ReadReference(path);Save(result);return result;}
        }
        return null;
    }
    sealed class DexConstants(byte[] data)
    {
        int U32(int position){Bounds(position,4);var value=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position,4));return checked((int)value);}
        int U16(int position){Bounds(position,2);return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position,2));}
        void Bounds(int position,int length){if(position<0 || length<0 || position>data.Length-length)throw new InvalidDataException("DEX bounds");}
        int Uleb(ref int position)
        {
            uint value=0;for(var i=0;i<5;i++){Bounds(position,1);var b=data[position++];value|=(uint)(b&127)<<(7*i);if((b&128)==0)return checked((int)value);}throw new InvalidDataException("DEX integer");
        }
        string String(int index)
        {
            if(index<0 || index>=U32(56))throw new InvalidDataException("DEX string index");
            var pos=U32(checked(U32(60)+index*4));_ = Uleb(ref pos);var start=pos;
            while(pos<data.Length && data[pos]!=0 && pos-start<16384)pos++;
            if(pos==data.Length || pos-start>=16384)throw new InvalidDataException("DEX string length");return Encoding.UTF8.GetString(data,start,pos-start);
        }
        string Type(int index){if(index<0||index>=U32(64))throw new InvalidDataException("DEX type index");return String(U32(checked(U32(68)+index*4)));}
        string? Value(ref int position,int depth=0)
        {
            if(depth>8)throw new InvalidDataException("DEX value depth");Bounds(position,1);var header=data[position++];var type=header&31;var width=(header>>5)+1;
            if(type is 0x1e or 0x1f)return null;
            if(type==0x1c){var size=Uleb(ref position);if(size>4096)throw new InvalidDataException("DEX array size");for(var i=0;i<size;i++)_ = Value(ref position,depth+1);return null;}
            if(type==0x1d){_ = Uleb(ref position);var size=Uleb(ref position);if(size>4096)throw new InvalidDataException("DEX annotation size");for(var i=0;i<size;i++){_ = Uleb(ref position);_ = Value(ref position,depth+1);}return null;}
            Bounds(position,width);ulong value=0;for(var i=0;i<width;i++)value|=(ulong)data[position++]<<(8*i);return type==0x17?String(checked((int)value)):null;
        }
        public Dictionary<string,string>? ReadBuildConfig()
        {
            if(data.Length<112 || Encoding.ASCII.GetString(data,0,4)!="dex\n")throw new InvalidDataException("DEX header");
            var count=U32(96);if(count>100000)throw new InvalidDataException("DEX class count");
            for(var i=0;i<count;i++)
            {
                var entry=checked(U32(100)+i*32);Bounds(entry,32);if(Type(U32(entry))!="Lcom/arris/sbc/BuildConfig;")continue;
                var position=U32(entry+24);var valuesPosition=U32(entry+28);if(position==0||valuesPosition==0)return null;
                var statics=Uleb(ref position);_ = Uleb(ref position);_ = Uleb(ref position);_ = Uleb(ref position);
                if(statics>10000)throw new InvalidDataException("DEX static field count");
                var fields=new List<string>();var index=0;
                for(var j=0;j<statics;j++)
                {
                    index=checked(index+Uleb(ref position));_ = Uleb(ref position);if(index>=U32(80))throw new InvalidDataException("DEX field index");
                    var field=checked(U32(84)+index*8);_ = U16(field);fields.Add(String(U32(field+4)));
                }
                var values=Uleb(ref valuesPosition);if(values>fields.Count)throw new InvalidDataException("DEX static values");
                var result=new Dictionary<string,string>();
                for(var j=0;j<values;j++){var text=Value(ref valuesPosition);if(text is not null && Names.Contains(fields[j]))result[fields[j]]=text;}
                return result;
            }
            return null;
        }
    }
}
