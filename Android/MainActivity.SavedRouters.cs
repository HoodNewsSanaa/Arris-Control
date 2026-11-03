namespace ArrisControl;

public sealed partial class MainActivity
{
    CancellationTokenSource? savedRouterMatching;
    string matchedNetwork="";

    void StartSavedRouterMatching()
    {
        savedRouterMatching?.Cancel();
        savedRouterMatching?.Dispose();
        savedRouterMatching=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _=WatchSavedRouters(savedRouterMatching.Token);
    }

    async Task WatchSavedRouters(CancellationToken token)
    {
        try
        {
            // Let the initial activity and any explicit CI self-test start first.
            await Task.Delay(500,token);
            while(!token.IsCancellationRequested)
            {
                if(Intent?.GetBooleanExtra("offlineSelfTest",false)==true || Intent?.GetBooleanExtra("updaterSelfTest",false)==true ||
                    Intent?.GetBooleanExtra("updateRetentionSelfTest",false)==true)return;
                var key=AndroidNetwork.ConnectionKey;
                if(key.Length==0)matchedNetwork="";
                else if(key!=matchedNetwork && !busy && !dirty && ble is null &&
                    GetSharedPreferences("connection",Android.Content.FileCreationMode.Private)!.GetBoolean("autoSavedRouters",true))
                {
                    var version=interactionVersion;
                    var records=CredentialStore.Find();
                    var candidates=records.Select(r=>r.Address).Concat(AndroidNetwork.Gateways())
                        .Where(AndroidNetwork.IsOnLocalSubnet).Distinct().Take(32).ToArray();
                    if(records.Count>0 && candidates.Length>0)
                    {
                        AndroidNetwork.UseLocal();
                        // Probe only known addresses and gateways. Never run a subnet scan in the background.
                        var endpoints=await RouterDiscovery.DiscoverApisAsync(candidates,null,token);
                        if(token.IsCancellationRequested)return;
                        if(key!=AndroidNetwork.ConnectionKey || version!=interactionVersion || busy || dirty || ble is not null)
                        {await Task.Delay(3000,token);continue;}
                        var matches=LocalAdminAccess.MatchSaved(records,endpoints);
                        var last=GetSharedPreferences("connection",Android.Content.FileCreationMode.Private)!.GetString("serial","");
                        var preferred=matches.Where(m=>m.Record.Serial==last).ToList();
                        if(preferred.Count==1)matches=preferred;
                        matchedNetwork=key;
                        if(matches.Count==1)
                        {
                            var match=matches[0];
                            await Run(()=>Connect(match.Record,match.Endpoint.Address,automatic:true),quiet:true);
                        }
                        else if(matches.Count>1)status.Text="Several saved routers are available. Choose one under Connections.";
                    }
                    else matchedNetwork=key;
                }
                await Task.Delay(3000,token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception ex) when(ex is RouterException or System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            // A background check must never prevent manual login or Bluetooth setup.
            // The next Wi-Fi change/resume may try again; no password guessing or write retries.
        }
    }
}
