using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace ArrisControl;
internal record ReleaseInfo(Version Version,string Tag,Uri Asset,Uri Checksums,long Size,string? Digest);
internal record StagedUpdate(string File,string SHA256,string Version);
internal record UpdatePlan(int ParentId,long ParentStartTicks,string Destination,string Candidate,string SHA256,string OriginalSHA256,string Version);

internal static class ReleaseUpdater
{
    public const string Repository="HoodNewsSanaa/Arris-Control";
    public const string AssetName="Arris-Control-win-x64.exe";
    public static readonly Version CurrentVersion=Normalize(Assembly.GetExecutingAssembly().GetName().Version??new Version(0,1,0));
    public static readonly string UpdateDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArrisControl","Updates");
    static readonly HttpClient Http=new(new HttpClientHandler{AllowAutoRedirect=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromMinutes(5)};
    static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(0,v.Build));
    internal static bool IsNewer(string tag,Version current,out Version version)
    {
        version=new Version(0,0,0);return Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$") && Version.TryParse(tag[1..],out version!) && version>Normalize(current);
    }
    internal static void ValidateAsset(Uri uri,string tag,string name)
    {
        var expected=$"/{Repository}/releases/download/{tag}/{name}";
        if(uri.Scheme!="https" || uri.Port!=443 || uri.Host!="github.com" || uri.UserInfo.Length!=0 || uri.Query.Length!=0 || uri.Fragment.Length!=0 || uri.AbsolutePath!=expected)
            throw new RouterException("The update asset does not belong to the configured GitHub release.");
    }
    static bool AllowedRedirect(Uri uri)=>uri.Scheme=="https" && uri.Port==443 && uri.UserInfo.Length==0 && uri.Fragment.Length==0 &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    static async Task<HttpResponseMessage> FetchAsync(Uri uri,CancellationToken token)
    {
        for(var i=0;i<6;i++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("Arris-Control/"+CurrentVersion);
            if(uri.Host=="api.github.com")request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next=response.Headers.Location is {IsAbsoluteUri:true} absolute?absolute:response.Headers.Location is not null?new Uri(uri,response.Headers.Location):null;
                response.Dispose();if(next is null || !AllowedRedirect(next))throw new RouterException("The update download redirected to an unexpected host.");uri=next;continue;
            }
            return response;
        }
        throw new RouterException("Too many update download redirects.");
    }
    static async Task<byte[]> BoundedAsync(HttpResponseMessage response,long maximum,CancellationToken token)
    {
        if(!response.IsSuccessStatusCode)throw new RouterException("GitHub update request returned HTTP "+(int)response.StatusCode+". The current app remains usable.");
        if(response.Content.Headers.ContentLength>maximum)throw new RouterException("The update response is larger than expected.");
        await using var stream=await response.Content.ReadAsStreamAsync(token);using var data=new MemoryStream();var buffer=new byte[32768];int count;
        while((count=await stream.ReadAsync(buffer,token))>0){if(data.Length+count>maximum)throw new RouterException("The update response exceeded its limit.");await data.WriteAsync(buffer.AsMemory(0,count),token);}return data.ToArray();
    }
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken token=default)
    {
        using var response=await FetchAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"),token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        var root=JsonNode.Parse(await BoundedAsync(response,2*1024*1024,token))!.AsObject();
        if(root["draft"]?.GetValue<bool>()==true || root["prerelease"]?.GetValue<bool>()==true)return null;
        var tag=Catalog.Text(root["tag_name"]);if(!IsNewer(tag,CurrentVersion,out var version))return null;
        var assets=(root["assets"] as JsonArray)?.OfType<JsonObject>().ToArray()??[];
        var asset=assets.SingleOrDefault(a=>Catalog.Text(a["name"])==AssetName)??throw new RouterException("The release has no Windows executable.");
        var sums=assets.SingleOrDefault(a=>Catalog.Text(a["name"])=="SHA256SUMS.txt")??throw new RouterException("The release has no checksum file.");
        var assetUri=new Uri(Catalog.Text(asset["browser_download_url"]));var sumsUri=new Uri(Catalog.Text(sums["browser_download_url"]));ValidateAsset(assetUri,tag,AssetName);ValidateAsset(sumsUri,tag,"SHA256SUMS.txt");
        var size=asset["size"]?.GetValue<long>()??0;if(size is <1024 or >268435456)throw new RouterException("Unexpected update executable size.");
        return new(version,tag,assetUri,sumsUri,size,Catalog.Text(asset["digest"]));
    }
    internal static string ParseChecksum(string text)
    {
        var entries=text.Split('\n').Select(l=>l.TrimEnd('\r')).Select(l=>Regex.Match(l,@"^([a-fA-F0-9]{64}) [ *]"+Regex.Escape(AssetName)+"$")).Where(m=>m.Success).ToArray();
        if(entries.Length!=1)throw new RouterException("A unique checksum for this executable was not found.");return entries[0].Groups[1].Value.ToUpperInvariant();
    }
    internal static void ValidateDigest(string expected,string actual,string? githubDigest)
    {
        if(!Regex.IsMatch(expected,"^[A-Fa-f0-9]{64}$") || !expected.Equals(actual,StringComparison.OrdinalIgnoreCase))throw new RouterException("Update checksum verification failed. The app was not replaced.");
        if(!string.IsNullOrEmpty(githubDigest) && (!githubDigest.StartsWith("sha256:",StringComparison.Ordinal) || !githubDigest[7..].Equals(actual,StringComparison.OrdinalIgnoreCase)))throw new RouterException("The downloaded executable does not match GitHub's asset digest.");
    }
    public static async Task<StagedUpdate> DownloadAsync(ReleaseInfo release,CancellationToken token=default)
    {
        if(release.Version<=CurrentVersion)throw new RouterException("Downgrades and same-version replacements are not installed.");
        ValidateAsset(release.Asset,release.Tag,AssetName);ValidateAsset(release.Checksums,release.Tag,"SHA256SUMS.txt");
        using var sums=await FetchAsync(release.Checksums,token);var expected=ParseChecksum(Encoding.UTF8.GetString(await BoundedAsync(sums,65536,token)));
        var directory=Path.Combine(UpdateDirectory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var target=Path.Combine(directory,"Arris-Control.download");bool complete=false;
        try
        {
            using var response=await FetchAsync(release.Asset,token);
            if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long size && size!=release.Size)throw new RouterException("Update download size or HTTP response was unexpected.");
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                var buffer=new byte[65536];long total=0;int count;
                while((count=await input.ReadAsync(buffer,token))>0){total+=count;if(total>release.Size)throw new RouterException("Update download exceeded its advertised size.");await output.WriteAsync(buffer.AsMemory(0,count),token);}
                if(total!=release.Size)throw new RouterException("The update download is incomplete.");
            }
            ValidateDigest(expected,Hash(target),release.Digest);
            using(var file=File.OpenRead(target))if(file.ReadByte()!='M' || file.ReadByte()!='Z')throw new RouterException("The update is not a Windows executable.");
            var info=FileVersionInfo.GetVersionInfo(target);if(!Version.TryParse(info.FileVersion,out var built) || Normalize(built)!=release.Version)throw new RouterException("The executable version does not match the release tag.");
            complete=true;return new(target,expected,release.Version.ToString());
        }
        finally{if(!complete && File.Exists(target))File.Delete(target);}
    }
    internal static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    public static void StartApply(StagedUpdate staged)
    {
        var executable=Environment.ProcessPath??throw new RouterException("Could not locate the running executable.");
        if(Path.GetFileName(executable).Equals("dotnet.exe",StringComparison.OrdinalIgnoreCase))throw new RouterException("Restart from the published executable to install updates.");
        ValidateDigest(staged.SHA256,Hash(staged.File),null);
        var directory=Path.GetDirectoryName(staged.File)!;var helper=Path.Combine(directory,"Arris-Control.Updater.exe");File.Copy(executable,helper,false);
        using var process=Process.GetCurrentProcess();var plan=new UpdatePlan(process.Id,process.StartTime.ToUniversalTime().Ticks,executable,staged.File,staged.SHA256,Hash(executable),staged.Version);
        var planFile=Path.Combine(directory,"update-plan.json");File.WriteAllText(planFile,JsonSerializer.Serialize(plan));
        var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=directory};start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(planFile);_ = Process.Start(start)??throw new RouterException("Could not start the update helper.");
    }
    internal static void ValidatePlan(UpdatePlan plan,string planFile)
    {
        var directory=Path.GetDirectoryName(Path.GetFullPath(planFile))!;
        if(!directory.StartsWith(Path.GetFullPath(UpdateDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(plan.Candidate)!=Path.Combine(directory,"Arris-Control.download") ||
            !new[]{"Arris-Control.exe",AssetName}.Contains(Path.GetFileName(plan.Destination),StringComparer.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(plan.Destination) || !Version.TryParse(plan.Version,out var version) || version<=CurrentVersion)
            throw new RouterException("Invalid update replacement plan.");
        ValidateDigest(plan.SHA256,Hash(plan.Candidate),null);
        if(Hash(plan.Destination)!=plan.OriginalSHA256)throw new RouterException("The installed executable changed before the update. It was not replaced.");
    }
    internal static void ReplaceFile(string candidate,string destination,string expectedHash)
    {
        ValidateDigest(expectedHash,Hash(candidate),null);
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".new";var backup=destination+"."+Guid.NewGuid().ToString("N")+".previous";
        try{File.Copy(candidate,temporary,false);ValidateDigest(expectedHash,Hash(temporary),null);File.Replace(temporary,destination,backup);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static int ApplyUpdate(string planFile)
    {
        try
        {
            if(new FileInfo(planFile).Length>16384)throw new RouterException("Update plan is too large.");
            var plan=JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planFile))??throw new RouterException("Missing update plan.");ValidatePlan(plan,planFile);
            try
            {
                using var parent=Process.GetProcessById(plan.ParentId);
                if(parent.StartTime.ToUniversalTime().Ticks!=plan.ParentStartTicks)throw new RouterException("The update's parent process no longer matches.");
                if(!parent.WaitForExit(30000))throw new RouterException("The app did not close; the update was not applied.");
            }
            catch(ArgumentException){}
            ValidatePlan(plan,planFile);ReplaceFile(plan.Candidate,plan.Destination,plan.SHA256);
            Process.Start(new ProcessStartInfo(plan.Destination){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(plan.Destination)!});return 0;
        }
        catch(Exception)
        {
            MessageBox.Show("The application update could not be applied. The existing app or its .previous backup remains available. Router settings and credentials were not modified.","Arris Control updater",MessageBoxButton.OK,MessageBoxImage.Information);return 1;
        }
    }
}
