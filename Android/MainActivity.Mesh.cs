using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using System.Text.Json.Nodes;
using Color=Android.Graphics.Color;

namespace ArrisControl;
public sealed partial class MainActivity
{
    void ShowMesh(Snapshot snapshot,CredentialRecord? previewRecord=null)
    {
        var topology=MeshTopology.Parse(snapshot,previewRecord??client!.Record);
        var card=Card("Mesh backhaul",$"{topology.Routers.Count} routers · {topology.Links.Count} reported connections · {topology.Time:HH:mm:ss}");
        var selected=Label(topology.Describe(topology.Routers[0]),14);
        var diagram=new MeshMapView(this,topology,node=>selected.Text=node.Name+"\n"+topology.Describe(node));
        card.AddView(diagram,new LinearLayout.LayoutParams(-1,Dp(380)));
        card.AddView(selected);
        card.AddView(Label("↓ Local AP → satellite   ↑ Satellite → local AP\nPHY link rates, not measured throughput. A dotted triangle guide means no connection was reported. This snapshot does not refresh automatically.",12));
        foreach(var node in topology.Routers)
            Action(card,node.Name+(node.Controller?" · Main":" · Satellite"),()=>{diagram.Select(node);selected.Text=node.Name+"\n"+topology.Describe(node);return Task.CompletedTask;});
        if(previewRecord is null)Action(content,"Add a mesh node",()=>{ShowAddNode(snapshot);return Task.CompletedTask;},Tone.Primary);
    }
    void ShowAddNode(Snapshot snapshot)
    {
        Shell("Add a mesh node");backAction=async()=>{currentPage=Catalog.Pages.First(p=>p.Id=="mesh");ShowRouter();await Read();};
        var intro=Card("Local node setup","Use Bluetooth to identify the new unit, then enable discovery on the authenticated main router and monitor its topology. No ARRIS account is used by these actions.");
        intro.AddView(Label("Full offline pairing is still unverified. Selecting EXTENDER does not by itself pair a node; a Bluetooth acknowledgement or discovery setting is not a joined-node result.",14));
        Action(intro,"1 · Find and inspect the node over Bluetooth",BleScanAndPick,Tone.Primary);
        var section=Settings.Sections(snapshot).FirstOrDefault(s=>s.Container=="AUTO_WOBA");
        if(section is not null){content.AddView(Label("2 · Review the controller discovery window",20,true));Editor(section);}
        else content.AddView(Label("The main router did not return its local discovery controls. Inspect its capabilities; no pairing command has been guessed.",14));
        Action(content,"3 · Refresh joined-node topology",async()=>{ble?.Dispose();ble=null;currentPage=Catalog.Pages.First(p=>p.Id=="mesh");ShowRouter();await Read();},Tone.Primary);
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
        readonly MainActivity owner;readonly MeshTopology mesh;readonly Action<MeshRouter> changed;
        readonly Paint paint=new(PaintFlags.AntiAlias);readonly List<(MeshRouter Node,float X,float Y)> points=[];
        string selected;float radius;
        public MeshMapView(MainActivity owner,MeshTopology mesh,Action<MeshRouter> changed):base(owner)
        {
            this.owner=owner;this.mesh=mesh;this.changed=changed;selected=mesh.Routers[0].Id;Clickable=true;
            ContentDescription="Mesh topology. "+string.Join(". ",mesh.Routers.Select(n=>n.Name+": "+mesh.Describe(n)));
        }
        public void Select(MeshRouter node){selected=node.Id;Invalidate();}
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
