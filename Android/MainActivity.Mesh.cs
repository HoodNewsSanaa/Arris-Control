using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using System.Text.Json.Nodes;
using Color=Android.Graphics.Color;

namespace ArrisControl;
public sealed partial class MainActivity
{
    sealed class SatelliteSetupState(CredentialRecord controller, string? pin)
    {
        public CredentialRecord Controller = controller;
        public string? Pin = pin;
        public BleRouterInspection? Inspection;
        public CloudDevice? Target;
        public string? IdentityError;
        public DateTimeOffset SelectedAt;
        public SatelliteJoinResult? Result;
    }
    SatelliteSetupState? satelliteSetup;
    void ShowMesh(Snapshot snapshot,CredentialRecord? previewRecord=null)
    {
        var reported=MeshTopology.Parse(snapshot,previewRecord??client!.Record);
        var names=GetSharedPreferences("mesh-node-names",FileCreationMode.Private)!;
        var topology=previewRecord is null?MeshNodeLabels.Apply(reported,key=>names.GetString(key,null)):reported;
        var card=Card("Mesh backhaul",$"{topology.Routers.Count} routers · {topology.Links.Count} reported connections · {topology.Time:HH:mm:ss}");
        var selectedId=topology.Routers[0].Id;
        var selected=Label(topology.Routers[0].Name+"\n"+topology.Describe(topology.Routers[0]),14);
        var diagram=new MeshMapView(this,topology,node=>{selectedId=node.Id;selected.Text=node.Name+"\n"+topology.Describe(node);});
        card.AddView(diagram,new LinearLayout.LayoutParams(-1,Dp(380)));
        card.AddView(selected);
        card.AddView(Label("↓ Local AP → satellite   ↑ Satellite → local AP\nPHY link rates, not measured throughput. A dotted triangle guide means no connection was reported. This snapshot does not refresh automatically.",12));
        var nodeButtons=new Dictionary<string,Button>();
        foreach(var node in topology.Routers)
            nodeButtons[node.Id]=Action(card,node.Name+(node.Controller?" · Main":" · Satellite"),()=>
            {
                var current=topology.Routers.First(n=>n.Id==node.Id);selectedId=current.Id;
                diagram.Select(current);selected.Text=current.Name+"\n"+topology.Describe(current);return Task.CompletedTask;
            });
        if(previewRecord is null)
        {
            Action(card,"Name selected node",async()=>
            {
                var node=topology.Routers.First(n=>n.Id==selectedId);
                var keys=MeshNodeLabels.Keys(node);
                if(keys.Length==0)throw new RouterException("This node has no stable identity for saving a name. Refresh its identity first.");
                var form=Column();form.SetPadding(Dp(20),Dp(4),Dp(20),Dp(4));
                var field=Input(form,"Name in this app · up to 48 characters",node.Name);
                form.AddView(Label("Saved on this phone for this router. Wi-Fi names and router settings are unchanged.",13));
                var done=new TaskCompletionSource<int>();
                var dialog=new AlertDialog.Builder(this)!.SetTitle("Name mesh node")!.SetView(form)!
                    .SetPositiveButton("Save",(_,_)=>done.TrySetResult(1))!
                    .SetNeutralButton("Use reported name",(_,_)=>done.TrySetResult(0))!
                    .SetNegativeButton("Cancel",(_,_)=>done.TrySetResult(-1))!.Create()!;
                dialog.CancelEvent+=(_,_)=>done.TrySetResult(-1);dialog.Show();
                var action=await done.Task;if(action<0)return;
                var value=action==0?"":MeshNodeLabels.Validate(field.Text??"");
                var editor=names.Edit()!;
                foreach(var key in keys)if(value.Length==0)editor.Remove(key);else editor.PutString(key,value);
                if(!editor.Commit())throw new RouterException("The node name could not be saved on this phone. Try again.");
                topology=MeshNodeLabels.Apply(reported,key=>names.GetString(key,null));
                foreach(var current in topology.Routers)nodeButtons[current.Id].Text=current.Name+(current.Controller?" · Main":" · Satellite");
                diagram.SetTopology(topology);
                var renamed=topology.Routers.First(n=>n.Id==selectedId);
                selected.Text=renamed.Name+"\n"+topology.Describe(renamed);
                status.Text=value.Length==0?"Using the router's reported name.":"Node name saved on this phone.";
            });
            Action(content,"Add a mesh node",()=>{ShowAddNode(snapshot);return Task.CompletedTask;},Tone.Primary);
        }
    }
    void ShowAddNode(Snapshot snapshot)
    {
        var controller = client!.Record;
        if (satelliteSetup is null || satelliteSetup.Controller.Serial != controller.Serial ||
            satelliteSetup.Controller.Mac != controller.Mac || satelliteSetup.Pin != client.SessionPin)
            satelliteSetup = new(controller, client.SessionPin);
        var setup = satelliteSetup;
        Shell("Add a mesh node");backAction=async()=>{currentPage=Catalog.Pages.First(p=>p.Id=="mesh");ShowRouter();await Read();};
        var intro=Card("Controller stays connected locally", controller.Address + " · " + controller.Serial);
        StatusRow(intro, "Controller onboarding", RouterStatus.Onboarding(client.OnboardingStatus, "Normal local login"));
        StatusRow(intro, "Controller onboarding service", RouterStatus.BluetoothService(client.BluetoothServiceStatus, "Normal local login"));
        intro.AddView(Label("The controller uses Wi-Fi/Ethernet administration throughout. Its Bluetooth service does not need to advertise. The phone uses Bluetooth only to inspect the new satellite. An incomplete controller flag does not block these inspection/discovery controls; pairing on that firmware state remains unverified.",14));
        Action(intro,"1 · Inspect the new satellite over Bluetooth",async()=>
        {
            var device = await PickBleDevice();
            if (device is null) return;
            try
            {
                await BleConnectAndInspect(device, inspection =>
                {
                    setup.Inspection = inspection; setup.Target = null; setup.Result = null; setup.IdentityError = null;
                    try
                    {
                        if (!inspection.Application.Trim().Equals("maxrouter", StringComparison.OrdinalIgnoreCase))
                            throw new RouterException("The selected unit did not identify itself as maxrouter.");
                        var target = new CloudDevice(inspection.Model, inspection.Serial, inspection.Mac);
                        BleSetupProtocol.ValidateIdentity(target);
                        if (target.Serial == controller.Serial || target.Mac == controller.Mac)
                            throw new RouterException("That is the connected controller. Select the new satellite instead.");
                        setup.Target = target; setup.SelectedAt = DateTimeOffset.UtcNow;
                    }
                    catch (RouterException ex) { setup.IdentityError = ex.Message; }
                    ShowAddNode(snapshot); return Task.CompletedTask;
                });
            }
            finally { ble?.Dispose(); ble = null; }
        },Tone.Primary);
        if (setup.Inspection is { } reading)
        {
            var node = Card("Selected satellite", setup.Target?.ToString() ?? "Identity unavailable");
            StatusRow(node, "New unit role", RouterStatus.Role(reading.Role, "Bluetooth inspection"));
            StatusRow(node, "New unit setup state", RouterStatus.BluetoothSetup(reading.Setup, "Bluetooth inspection"));
            if (setup.IdentityError is not null) node.AddView(Label(setup.IdentityError + " Automatic join verification needs readable serial and WAN MAC; they are not guessed from the advertisement.",14,color:Palette.Warning));
            foreach (var error in reading.Errors) node.AddView(Label(MaxBle.Short(error.Key) + ": " + error.Value,12,color:Palette.Warning));
            Action(node,"View Bluetooth diagnostic log",()=>{ShowReport("Satellite Bluetooth diagnostics","Serial/MAC values are withheld in this log.",BleLog.Export());return Task.CompletedTask;});
        }
        var discovery = Card("2 · Controller discovery", "Open the controller's local discovery window. This does not supply backhaul credentials, complete satellite provisioning, or prove a node joined.");
        var section=Settings.Sections(snapshot).FirstOrDefault(s=>s.Container=="AUTO_WOBA");
        if(section is not null)
        {
            discovery.AddView(Label("Reported discovery: " + (Catalog.Text(section.Baseline["ENABLE"])=="1"?"Enabled":"Off or unknown"),14));
            Action(discovery,"Review opening discovery for 3 minutes",async()=>
            {
                var fresh = await client.ReadPageAsync(Catalog.Pages.First(p=>p.Id=="mesh"),lifetime.Token);
                var current = Settings.Sections(fresh).FirstOrDefault(s=>s.Container=="AUTO_WOBA")
                    ?? throw new RouterException("The controller did not return its discovery controls. Nothing was changed.");
                var plan = await client.PrepareAsync(current,new(){["ENABLE"]="1",["TIMEOUT"]="180"},token:lifetime.Token);
                if(!await Confirm("Open controller discovery?",plan.Preview,"Open discovery once"))return;
                var result = await client.ApplyAsync(plan,lifetime.Token);
                await Message("Controller discovery",result);
                ShowAddNode(await client.ReadPageAsync(Catalog.Pages.First(p=>p.Id=="mesh"),lifetime.Token));
            });
        }
        else discovery.AddView(Label("The controller did not return discovery controls. Refresh its status to see the read result. No pairing command is guessed.",14));
        var verification = Card("3 · Verify the selected satellite", "A verified result requires a fresh network-map entry and a matching active backhaul association. Wired backhaul without a reported active association remains unverified.");
        if(setup.Target is not null)
            Action(verification,"Check selected satellite on the local network",async()=>
            {
                var fresh=await client.ReadPageAsync(Catalog.Pages.First(p=>p.Id=="mesh"),lifetime.Token);
                setup.Result=SatelliteJoin.Verify(fresh,client.Record,setup.Target,setup.SelectedAt);
                ShowAddNode(fresh);
            },Tone.Primary);
        else verification.AddView(Label("Inspect and identify the new satellite first.",14));
        if(setup.Result is { } checkedJoin)
        {
            verification.AddView(Label(checkedJoin.State,20,true,color:checkedJoin.Joined?Palette.Success:Palette.Warning));
            verification.AddView(Label(checkedJoin.Detail,14));
        }
        Action(content,"Refresh controller flags and discovery status",async()=>
        {
            await client.ReconnectAsync(lifetime.Token);
            ShowAddNode(await client.ReadPageAsync(Catalog.Pages.First(p=>p.Id=="mesh"),lifetime.Token));
        });
        content.AddView(Label("Full offline satellite provisioning remains unverified. No controller Bluetooth connection, role reassignment, cloud configuration or backhaul-password write is performed by this screen.",12,color:Palette.Muted));
        Action(content,"Back to mesh",backAction!);
    }
    void ShowMeshRenderFixture()
    {
        var record=new CredentialRecord("fixture","TESTSERIAL123","001122334455","192.168.7.1");
        var data=new Dictionary<string,JsonObject>{
            ["AUTO_WOBA_SAT_STATUS_ALL"]=JsonNode.Parse("""{"AUTO_WOBA_SAT_STATUS":{"SAT_LIST":[{"MAX_SERIAL_NO":"SATELLITE001","MAX_WAN_MAC":"001122334466","MAX_WiFi_MAC_BH":"001122334467"},{"MAX_SERIAL_NO":"SATELLITE002","MAX_WAN_MAC":"001122334477","MAX_WiFi_MAC_BH":"001122334478"}]}}""")!.AsObject(),
            ["LINKSPEEDBH_ALL"]=JsonNode.Parse("""{"LINKSPEEDBH":[{"ACTIVE":"true","STAADDRESS":"001122334467","DOWNLINKRATE":"2402000","UPLINKRATE":"2161000","SIGNALSTRENGTH":"-56"},{"ACTIVE":"true","STAADDRESS":"001122334478","DOWNLINKRATE":"1800000","UPLINKRATE":"1600000","SIGNALSTRENGTH":"-61"}]}""")!.AsObject()};
        var snapshot=new Snapshot(Catalog.Pages.First(p=>p.Id=="mesh"),data,[],[],DateTimeOffset.UtcNow);
        Shell("Mesh & nodes");content.AddView(Label("Fictional offline rendering fixture — no router changes",12));ShowMesh(snapshot,record);
    }
    sealed class MeshMapView : View
    {
        readonly MainActivity owner;MeshTopology mesh;readonly Action<MeshRouter> changed;
        readonly Paint paint=new(PaintFlags.AntiAlias);readonly List<(MeshRouter Node,float X,float Y)> points=[];
        string selected;float radius;
        public MeshMapView(MainActivity owner,MeshTopology mesh,Action<MeshRouter> changed):base(owner)
        {
            this.owner=owner;this.mesh=mesh;this.changed=changed;selected=mesh.Routers[0].Id;Clickable=true;
            ContentDescription="Mesh topology. "+string.Join(". ",mesh.Routers.Select(n=>n.Name+": "+mesh.Describe(n)));
        }
        public void Select(MeshRouter node){selected=node.Id;Invalidate();}
        public void SetTopology(MeshTopology topology)
        {
            mesh=topology;
            ContentDescription="Mesh topology. "+string.Join(". ",mesh.Routers.Select(n=>n.Name+": "+mesh.Describe(n)));
            Invalidate();
        }
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);points.Clear();var visible=mesh.Routers.Take(8).ToArray();radius=owner.Dp(28);
            for(var i=0;i<visible.Length;i++)
            {
                float x,y;
                if(visible.Length<=3){x=visible.Length==2?.5f:i==0?.5f:i==1?.18f:.82f;y=i==0?.15f:.73f;}
                else{var angle=-Math.PI/2+2*Math.PI*i/visible.Length;x=.5f+.35f*(float)Math.Cos(angle);y=.45f+.31f*(float)Math.Sin(angle);}
                points.Add((visible[i],Width*x,Height*y));
            }
            // The third side of the approved triangle is a neutral guide, not a claimed link.
            if(points.Count==3 && !mesh.Links.Any(l=>(l.Parent==points[1].Node.Id&&l.Child==points[2].Node.Id)||(l.Parent==points[2].Node.Id&&l.Child==points[1].Node.Id)))
            {
                var a=points[1];var b=points[2];paint.Color=Palette.SurfaceStroke;paint.StrokeWidth=owner.Dp(1);
                using(var dash=new DashPathEffect([owner.Dp(4),owner.Dp(4)],0))
                {paint.SetPathEffect(dash);canvas.DrawLine(a.X,a.Y,b.X,b.Y,paint);paint.SetPathEffect(null);}
                var mx=(a.X+b.X)/2;paint.Color=Palette.Surface;canvas.DrawRect(mx-owner.Dp(53),a.Y-owner.Dp(13),mx+owner.Dp(53),a.Y+owner.Dp(13),paint);
                Text(canvas,"Not reported",mx,a.Y+owner.Dp(4),11,Palette.Muted);
            }
            foreach(var link in mesh.Links)
            {
                var a=points.FindIndex(p=>p.Node.Id==link.Parent);var b=points.FindIndex(p=>p.Node.Id==link.Child);if(a<0||b<0)continue;
                var from=points[a];var to=points[b];paint.Color=Palette.Muted;paint.StrokeWidth=owner.Dp(2);canvas.DrawLine(from.X,from.Y,to.X,to.Y,paint);
                var mx=(from.X+to.X)/2;var my=(from.Y+to.Y)/2;
                var down=link.Down=="Not reported"?"Rate not reported":"↓ "+link.Down;
                var up=link.Up=="Not reported"?link.Kind:"↑ "+link.Up;
                paint.Color=Palette.Surface;canvas.DrawRect(mx-owner.Dp(74),my-owner.Dp(20),mx+owner.Dp(74),my+owner.Dp(26),paint);
                Text(canvas,down,mx,my,12,Palette.Text);Text(canvas,up,mx,my+owner.Dp(18),11,Palette.Muted);
            }
            foreach(var p in points)
            {
                paint.SetStyle(Paint.Style.Fill);paint.Color=Palette.Surface;canvas.DrawCircle(p.X,p.Y,radius,paint);
                paint.SetStyle(Paint.Style.Stroke);paint.StrokeWidth=owner.Dp(p.Node.Id==selected?2:1);paint.Color=p.Node.Id==selected?Palette.Accent:Palette.SurfaceStroke;canvas.DrawCircle(p.X,p.Y,radius,paint);
                paint.SetStyle(Paint.Style.Fill);paint.Color=Palette.Muted;canvas.DrawRoundRect(p.X-owner.Dp(8),p.Y-owner.Dp(16),p.X+owner.Dp(8),p.Y+owner.Dp(16),owner.Dp(5),owner.Dp(5),paint);
                paint.Color=Palette.Surface;canvas.DrawRect(p.X-owner.Dp(3),p.Y+owner.Dp(10),p.X+owner.Dp(3),p.Y+owner.Dp(12),paint);
                var name=p.Node.Name.Length>18?p.Node.Name[..17]+"…":p.Node.Name;
                Text(canvas,name,p.X,p.Y+radius+owner.Dp(21),13,Palette.Text);
                Text(canvas,p.Node.Controller?"Main router":"Satellite",p.X,p.Y+radius+owner.Dp(39),11,Palette.Muted);
            }
            if(mesh.Links.Count==0)Text(canvas,"Backhaul arrangement not reported",Width/2f,Height*.44f,12,Palette.Muted);
        }
        void Text(Canvas canvas,string text,float x,float y,int size,Color color)
        {
            paint.SetStyle(Paint.Style.Fill);paint.TextAlign=Paint.Align.Center;paint.TextSize=size*Resources!.DisplayMetrics!.ScaledDensity;paint.Color=color;canvas.DrawText(text,x,y,paint);
        }
        public override bool OnTouchEvent(MotionEvent? e)
        {
            if(e?.Action==MotionEventActions.Up)
            {
                var hit=points.FirstOrDefault(p=>Math.Abs(e.GetX()-p.X)<owner.Dp(60)&&Math.Abs(e.GetY()-p.Y)<owner.Dp(60));
                if(hit.Node is not null){Select(hit.Node);changed(hit.Node);PerformClick();}return true;
            }
            return e?.Action==MotionEventActions.Down || base.OnTouchEvent(e);
        }
        public override bool PerformClick()=>base.PerformClick();
    }
}
