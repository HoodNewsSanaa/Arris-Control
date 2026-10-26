using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace ArrisControl;

internal static class Theme
{
    public static Brush Ink=new SolidColorBrush(Color.FromRgb(22,42,63));
    public static Brush Muted=new SolidColorBrush(Color.FromRgb(93,111,131));
    public static Brush Blue=new SolidColorBrush(Color.FromRgb(34,99,179));
    public static void Apply(Application app)
    {
        var button=new Style(typeof(Button));button.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(14,8,14,8)));button.Setters.Add(new Setter(Control.MarginProperty,new Thickness(0,0,8,0)));button.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.White));button.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(207,217,230))));app.Resources.Add(typeof(Button),button);
        var textbox=new Style(typeof(TextBox));textbox.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(9,7,9,7)));textbox.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(203,215,229))));app.Resources.Add(typeof(TextBox),textbox);
        var combo=new Style(typeof(ComboBox));combo.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));app.Resources.Add(typeof(ComboBox),combo);
    }
    public static TextBlock Text(string text,double size=14,Brush? color=null)=>new(){Text=text,FontSize=size,Foreground=color??Ink,TextWrapping=TextWrapping.Wrap};
    public static Border Card(UIElement child)=>new(){Child=child,Background=Brushes.White,CornerRadius=new CornerRadius(8),BorderBrush=new SolidColorBrush(Color.FromRgb(222,229,239)),BorderThickness=new Thickness(1),Padding=new Thickness(20),Margin=new Thickness(0,0,0,14)};
    public static Button Button(string text,RoutedEventHandler click,bool primary=false){var b=new Button{Content=text};if(primary){b.Background=Blue;b.Foreground=Brushes.White;b.BorderBrush=Blue;}b.Click+=click;return b;}
}

internal sealed class MainWindow : Window
{
    readonly ComboBox routerPicker=new(){MinWidth=325};readonly ListBox nav=new(){BorderThickness=new Thickness(0),Background=Brushes.Transparent,Foreground=Brushes.White,DisplayMemberPath="Name",FontSize=15};
    readonly TextBlock title=Theme.Text("Overview",29),description=Theme.Text("",14,Theme.Muted),status=Theme.Text("Select a router to connect locally.",14);
    readonly StackPanel content=new();readonly TextBox filter=new();readonly DataGrid fullGrid=GridFor(("Field","Field","2*"),("Value","Value","*"),("Source","Source","190"));
    readonly DataGrid readGrid=GridFor(("Query","Key","*"),("Result","Status","180"),("Read at","Time","180"));
    readonly TabControl tabs=new();readonly Button refresh,import,export,updates;readonly ProgressBar progress=new(){Height=3,IsIndeterminate=true,Visibility=Visibility.Collapsed};
    readonly Dictionary<string,Snapshot> snapshots=new();readonly bool testMode;readonly CancellationTokenSource cancellation=new();
    RouterClient? client;CredentialRecord? activeRecord;Page page=Catalog.Pages[0];Snapshot? current;bool busy,dirty,rendering,changingSelection;

    public MainWindow(bool testMode=false)
    {
        this.testMode=testMode;Title="Arris Control · Local router manager";Width=1280;Height=860;MinWidth=1100;MinHeight=680;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Segoe UI");FontSize=14;Background=new SolidColorBrush(Color.FromRgb(243,246,250));
        var root=new Grid{Background=Background};root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(225)});root.ColumnDefinitions.Add(new ColumnDefinition());Content=root;
        var side=new DockPanel{Margin=new Thickness(18,27,14,20)};var sideBorder=new Border{Background=new SolidColorBrush(Color.FromRgb(21,41,62)),Child=side};root.Children.Add(sideBorder);
        var brand=new StackPanel{Margin=new Thickness(0,0,0,26)};brand.Children.Add(Theme.Text("ARRIS CONTROL",21,Brushes.White));brand.Children.Add(Theme.Text("SURFboard mAX · Windows",12,Brushes.LightSteelBlue));DockPanel.SetDock(brand,Dock.Top);side.Children.Add(brand);
        var bottom=new StackPanel{Margin=new Thickness(0,22,0,0)};bottom.Children.Add(Theme.Text("LOCAL MANAGEMENT",11,Brushes.Aquamarine));bottom.Children.Add(Theme.Text("v"+ReleaseUpdater.CurrentVersion,12,Brushes.LightSteelBlue));DockPanel.SetDock(bottom,Dock.Bottom);side.Children.Add(bottom);
        nav.ItemsSource=Catalog.Pages;var itemStyle=new Style(typeof(ListBoxItem));itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,12,10,12)));itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,2,0,2)));nav.ItemContainerStyle=itemStyle;side.Children.Add(nav);
        var main=new Grid{Margin=new Thickness(25,22,25,16)};Grid.SetColumn(main,1);root.Children.Add(main);
        foreach(var h in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})main.RowDefinitions.Add(new RowDefinition{Height=h});
        var toolbar=new DockPanel{Margin=new Thickness(0,0,0,22)};main.Children.Add(toolbar);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Right);toolbar.Children.Add(buttons);
        updates=Theme.Button("Updates",async(_,_)=>await CheckUpdatesAsync());export=Theme.Button("Export coverage",(_,_)=>ExportCoverage());refresh=Theme.Button("Refresh",async(_,_)=>await RefreshAsync(),true);buttons.Children.Add(updates);buttons.Children.Add(export);buttons.Children.Add(refresh);
        var connection=new StackPanel{Orientation=Orientation.Horizontal};connection.Children.Add(routerPicker);import=Theme.Button("Import credential",(_,_)=>ImportCredential());import.Margin=new Thickness(10,0,0,0);connection.Children.Add(import);toolbar.Children.Add(connection);
        var heading=new StackPanel{Margin=new Thickness(0,0,0,14)};heading.Children.Add(title);description.Margin=new Thickness(0,7,0,0);heading.Children.Add(description);Grid.SetRow(heading,1);main.Children.Add(heading);
        var message=new StackPanel();message.Children.Add(status);progress.Margin=new Thickness(0,8,0,0);message.Children.Add(progress);var messageBorder=new Border{Background=new SolidColorBrush(Color.FromRgb(228,239,252)),Padding=new Thickness(13,10,13,10),CornerRadius=new CornerRadius(5),Child=message,Margin=new Thickness(0,0,0,14)};Grid.SetRow(messageBorder,2);main.Children.Add(messageBorder);
        tabs.Items.Add(new TabItem{Header="Settings",Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(2,16,15,0)}});
        var advanced=new DockPanel();var search=new DockPanel{Margin=new Thickness(0,12,0,10)};search.Children.Add(Theme.Text("Filter fields  "));search.Children.Add(filter);DockPanel.SetDock(search,Dock.Top);advanced.Children.Add(search);advanced.Children.Add(fullGrid);tabs.Items.Add(new TabItem{Header="Full settings",Content=advanced});Grid.SetRow(tabs,3);main.Children.Add(tabs);
        var details=new Expander{Header="Read results",Content=readGrid,Margin=new Thickness(0,12,0,0)};readGrid.MaxHeight=130;Grid.SetRow(details,4);main.Children.Add(details);
        var foot=Theme.Text("Local access · Credentials protected by Windows · Changes are sent only after Save and confirmation",12,Theme.Muted);foot.Margin=new Thickness(0,12,0,0);Grid.SetRow(foot,5);main.Children.Add(foot);
        filter.TextChanged+=(_,_)=>FilterRows();nav.SelectionChanged+=async(_,_)=>await SelectPageAsync();routerPicker.SelectionChanged+=async(_,_)=>await SelectRouterAsync();
        Loaded+=async(_,_)=>{if(!testMode){routerPicker.ItemsSource=CredentialStore.Find();if(routerPicker.Items.Count>0)routerPicker.SelectedIndex=0;else ShowNoCredential();}await Task.CompletedTask;};
        Closing+=OnClosing;Closed+=(_,_)=>{cancellation.Cancel();client?.Dispose();cancellation.Dispose();};
        changingSelection=true;nav.SelectedIndex=0;changingSelection=false;description.Text=page.Description;
    }
    static DataGrid GridFor(params (string Header,string Property,string Width)[] columns)
    {
        var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,BorderThickness=new Thickness(1),RowHeight=31,EnableRowVirtualization=true,ClipboardCopyMode=DataGridClipboardCopyMode.IncludeHeader};
        foreach(var col in columns){var w=col.Width.EndsWith('*')?new DataGridLength(col.Width=="*"?1:double.Parse(col.Width[..^1]),DataGridLengthUnitType.Star):new DataGridLength(double.Parse(col.Width));grid.Columns.Add(new DataGridTextColumn{Header=col.Header,Binding=new Binding(col.Property),Width=w,MinWidth=75});}return grid;
    }
    bool DiscardEdits()=>!dirty || MessageBox.Show(this,"Discard the unsaved edits on this page?","Unsaved settings",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    void OnClosing(object? sender,CancelEventArgs e){if(!DiscardEdits())e.Cancel=true;}
    async Task SelectPageAsync()
    {
        if(changingSelection || nav.SelectedItem is not Page selected)return;
        if(!DiscardEdits()){changingSelection=true;nav.SelectedItem=page;changingSelection=false;return;}
        page=selected;dirty=false;title.Text=page.Name;description.Text=page.Description;await RefreshAsync();
    }
    async Task SelectRouterAsync()
    {
        if(testMode || busy || changingSelection || routerPicker.SelectedItem is not CredentialRecord r)return;
        if(!DiscardEdits()){changingSelection=true;routerPicker.SelectedItem=activeRecord;changingSelection=false;return;}
        dirty=false;activeRecord=r;client?.Dispose();client=new RouterClient(r);snapshots.Clear();await RefreshAsync();
    }
    void SetBusy(bool value)
    {
        busy=value;nav.IsEnabled=!value;routerPicker.IsEnabled=!value;refresh.IsEnabled=!value;import.IsEnabled=!value;export.IsEnabled=!value && snapshots.Count>0;updates.IsEnabled=!value;content.IsEnabled=!value;progress.Visibility=value?Visibility.Visible:Visibility.Collapsed;
    }
    void ShowNoCredential()
    {
        content.Children.Clear();var box=new StackPanel();box.Children.Add(Theme.Text("Connect with your existing local credential",22));box.Children.Add(Theme.Text("Import the encrypted JSON credential created by the Windows local-access setup. It must belong to this Windows account and PC. No ARRIS cloud sign-in is needed.",14,Theme.Muted));content.Children.Add(Theme.Card(box));status.Text="No protected router credential found. Use Import credential.";
    }
    async Task RefreshAsync(bool discard=true)
    {
        if(busy)return;if(discard && !DiscardEdits())return;
        if(client is null){ShowNoCredential();return;}
        SetBusy(true);current=null;content.Children.Clear();fullGrid.ItemsSource=null;readGrid.ItemsSource=null;status.Text="Reading "+page.Name.ToLowerInvariant()+" directly from the router…";
        try
        {
            var result=await client.ReadPageAsync(page,cancellation.Token);current=result;snapshots[page.Id]=result;dirty=false;Render(result);
            var ok=result.Reads.Count(r=>r.Readable);status.Text=$"Local read complete · {ok}/{result.Reads.Count} queries · {result.Time:HH:mm:ss} · Cloud requests: 0";
            if(result.Reads.Any(r=>r.Status=="SessionTimeout"))status.Text+=" · Session expired; refresh to reconnect.";
        }
        catch(Exception ex){status.Text=FriendlyError(ex);}
        finally{SetBusy(false);}
    }
    static string FriendlyError(Exception ex)=>ex is RouterException?ex.Message:ex is OperationCanceledException?"The local request timed out or was cancelled. No save was retried.":"Could not read the router. Check the LAN connection and Windows credential. No cloud fallback was used.";
    void ImportCredential()
    {
        if(!DiscardEdits())return;
        var dialog=new OpenFileDialog{Title="Select the Windows-protected local router credential",Filter="Encrypted credential (*.json)|*.json",Multiselect=false};
        if(dialog.ShowDialog(this)!=true)return;
        try{var record=CredentialStore.Import(dialog.FileName);var list=CredentialStore.Find();routerPicker.ItemsSource=list;routerPicker.SelectedItem=list.First(r=>r.Serial==record.Serial && r.Mac==record.Mac);dirty=false;}
        catch(Exception ex){MessageBox.Show(this,ex is RouterException?ex.Message:"Could not import the protected credential.","Import credential",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    void FilterRows()
    {
        var query=filter.Text.Trim();fullGrid.ItemsSource=current?.Rows.Where(r=>query.Length==0 || r.Field.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Value.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Source.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray()??[];
    }
    static string Humanize(string field)
    {
        var leaf=field.Split('.').Last();var text=Regex.Replace(leaf,"([a-z])([A-Z])","$1 $2").Replace('_',' ').ToLowerInvariant();
        return text.Length>0?char.ToUpperInvariant(text[0])+text[1..]:field;
    }
    void Render(Snapshot snapshot)
    {
        rendering=true;content.Children.Clear();FilterRows();readGrid.ItemsSource=snapshot.Reads;
        if(page.Id=="overview")RenderOverview(snapshot);
        else if(page.Id=="mesh")RenderTopology(snapshot);
        else
        {
            foreach(var section in Settings.Sections(snapshot))content.Children.Add(BuildSection(section));
            foreach(var def in Settings.Definitions.Where(d=>d.Collection && snapshot.Data.ContainsKey(d.Key)))
            {
                var add=Theme.Button("Add "+def.Title.ToLowerInvariant(),(_,_)=>
                {
                    var initial=new JsonObject();foreach(var field in def.Fields)initial[field.Key]=field.Kind=="toggle"?"1":field.Kind=="choice"?field.Choices![0].Value:"";
                    content.Children.Insert(0,BuildSection(new(def,def.Container,-1,"",initial,"New "+def.Title.ToLowerInvariant()),true));dirty=true;
                });content.Children.Add(add);
            }
            var editableKeys=Settings.Sections(snapshot).Select(s=>s.Definition.Key).ToHashSet();
            var readOnly=snapshot.Rows.Where(r=>!editableKeys.Contains(r.Source)&&r.Value!="[hidden]").ToArray();
            if(readOnly.Length>0)
            {
                var info=new StackPanel();info.Children.Add(Theme.Text(page.Id=="capabilities"?"Firmware-reported capabilities":"More information",20));
                var table=GridFor(("Information","Label","*"),("Value","Value","*"));table.Margin=new Thickness(0,13,0,0);table.MaxHeight=500;table.ItemsSource=readOnly.Select(r=>new{Label=Humanize(r.Field),r.Value}).ToArray();info.Children.Add(table);content.Children.Add(Theme.Card(info));
            }
        }
        if(content.Children.Count==0)content.Children.Add(Theme.Card(Theme.Text("No supported setting data was returned for this page. Check Read results. Full settings retains any returned fields.")));
        rendering=false;
    }
    void RenderOverview(Snapshot snapshot)
    {
        string Get(string key,string container,string field)=>snapshot.Data.TryGetValue(key,out var data)?Catalog.Text(data[container]?[field]):"Unavailable";
        var cards=new WrapPanel();
        void Tile(string label,string value){var box=new StackPanel{Width=230};box.Children.Add(Theme.Text(label,13,Theme.Muted));var text=Theme.Text(value,21);text.Margin=new Thickness(0,9,0,0);box.Children.Add(text);var card=Theme.Card(box);card.Margin=new Thickness(0,0,14,14);cards.Children.Add(card);}
        var mode=Get("DEVICEOPERMODE_ALL","DEVICEOPERMODE","MODE");Tile("Routing",mode=="DUAL"?"Router · IPv4 + IPv6":mode=="IPV4ONLY"?"Router · IPv4 only":mode=="BRIDGE"?"Bridge mode":mode);
        Tile("Installed firmware",Get("FIRMWARE_ALL","FIRMWARE","SW_VERSION"));Tile("Internet connection",Get("DEVICESTATUS_ALL","DEVICESTATUS","WANSTATUS"));Tile("IPv4 DNS",Get("NETWORKDNS_ALL","NETWORKDNS","DNSIPV4SERVERS"));Tile("IPv6 DNS mode",Get("NETWORKDNS_ALL","NETWORKDNS","DYNAMICV6")=="1"?"Automatic":"Manual / see DNS settings");content.Children.Add(cards);
        var shortcuts=new StackPanel();shortcuts.Children.Add(Theme.Text("Manage your network",21));var row=new WrapPanel{Margin=new Thickness(0,14,0,0)};
        foreach(var id in new[]{"wifi","internet","lan","ports"}){var target=Catalog.Pages.First(p=>p.Id==id);row.Children.Add(Theme.Button(target.Name,(_,_)=>nav.SelectedItem=target));}shortcuts.Children.Add(row);content.Children.Add(Theme.Card(shortcuts));
        content.Children.Add(Theme.Card(Theme.Text("Enabling bridge mode is blocked. Local access while bridged and recovery after switching modes have not been verified.",14,Theme.Muted)));
    }
    void RenderTopology(Snapshot snapshot)
    {
        if(snapshot.Data.TryGetValue("NETWORKMAP_ALL",out var data) && data["HNC"] is JsonObject root)
        {
            TreeViewItem Node(JsonObject node,int depth)
            {
                var label=Catalog.Text(node["text"]);if(label.Length==0)label="Network node";var details=node["data"] as JsonObject;
                if(details is not null){var count=Catalog.Text(details["numClients"]);var channel=Catalog.Text(details["channel"]);if(count.Length>0)label+=" · "+count+" clients";if(channel.Length>0)label+=" · channel "+channel;}
                var item=new TreeViewItem{Header=label,IsExpanded=depth<3,Margin=new Thickness(0,5,0,5)};
                if(depth<12 && node["children"] is JsonArray children)foreach(var child in children.OfType<JsonObject>())item.Items.Add(Node(child,depth+1));return item;
            }
            var tree=new TreeView{BorderThickness=new Thickness(0),MaxHeight=500};tree.Items.Add(Node(root,0));content.Children.Add(Theme.Card(tree));
        }
        var table=GridFor(("Information","Label","*"),("Value","Value","*"));table.MaxHeight=350;table.ItemsSource=snapshot.Rows.Where(r=>r.Source!="NETWORKMAP_ALL").Select(r=>new{Label=Humanize(r.Field),r.Value}).ToArray();content.Children.Add(Theme.Card(table));
    }
    Border BuildSection(SettingSection section,bool adding=false)
    {
        var stack=new StackPanel();stack.Children.Add(Theme.Text(section.Title,20));
        var getters=new Dictionary<string,Func<string>>();var controls=new Dictionary<string,Control>();
        foreach(var field in section.Definition.Fields)
        {
            if(!adding && !section.Baseline.ContainsKey(field.Key) && field.Kind!="password")continue;
            var old=Catalog.Text(section.Baseline[field.Key]);var row=new Grid{Margin=new Thickness(0,16,0,0)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(235)});row.ColumnDefinitions.Add(new ColumnDefinition());var label=Theme.Text(field.Label);label.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(label);
            Control editor;Func<string> getter;
            if(field.Kind=="toggle")
            {
                var check=new CheckBox{Content="Enabled",IsThreeState=old is not ("0" or "1"),IsChecked=old=="1"?true:old=="0"?false:null,VerticalAlignment=VerticalAlignment.Center};check.Checked+=MarkDirty;check.Unchecked+=MarkDirty;check.Indeterminate+=MarkDirty;editor=check;getter=()=>check.IsChecked==true?"1":check.IsChecked==false?"0":old;
            }
            else if(field.Kind=="choice")
            {
                var combo=new ComboBox{ItemsSource=field.Choices,SelectedValuePath="Value",SelectedValue=old,MinWidth=230};combo.SelectionChanged+=(_,_)=>{if(!rendering)dirty=true;};editor=combo;getter=()=>combo.SelectedValue as string??old;
            }
            else if(field.Kind=="password")
            {
                var password=new PasswordBox{Padding=new Thickness(9,7,9,7)};password.PasswordChanged+=(_,_)=>{if(!rendering)dirty=true;};editor=password;getter=()=>password.Password;
            }
            else if(field.Kind is "dns4" or "dns6")
            {
                var parts=old.Split(',');var first=new TextBox{Text=parts[0],ToolTip="Primary DNS server",MinWidth=120};var second=new TextBox{Text=parts.Length>1?parts[1]:"",ToolTip="Secondary DNS server (optional)",MinWidth=120};first.TextChanged+=MarkDirty;second.TextChanged+=MarkDirty;
                var pair=new Grid();pair.ColumnDefinitions.Add(new ColumnDefinition());pair.ColumnDefinitions.Add(new ColumnDefinition());second.Margin=new Thickness(8,0,0,0);Grid.SetColumn(second,1);pair.Children.Add(first);pair.Children.Add(second);editor=new ContentControl{Content=pair};getter=()=>string.Join(',',new[]{first.Text.Trim(),second.Text.Trim()}.Where(s=>s.Length>0));
            }
            else{var text=new TextBox{Text=old};text.TextChanged+=MarkDirty;editor=text;getter=()=>text.Text.Trim();}
            getters[field.Key]=getter;controls[field.Key]=editor;Grid.SetColumn(editor,1);row.Children.Add(editor);stack.Children.Add(row);
            if(field.Hint.Length>0){var hint=Theme.Text(field.Hint,12,Theme.Muted);hint.Margin=new Thickness(235,5,0,0);stack.Children.Add(hint);}
        }
        if(section.Container=="NETWORKDNS")foreach(var family in new[]{"4","6"})
        {
            if(controls.TryGetValue("DYNAMICV"+family,out var control) && control is ComboBox combo && controls.TryGetValue("DNSIPV"+family+"SERVERS",out var servers))
            {void Update()=>servers.IsEnabled=(combo.SelectedValue as string)=="0";combo.SelectionChanged+=(_,_)=>Update();Update();}
        }
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,20,0,0)};
        actions.Children.Add(Theme.Button(adding?"Add rule":"Save changes",async(_,_)=>await SaveSectionAsync(section,getters,adding?"ADD":"MODIFY"),true));
        if(section.Definition.Collection&&!adding)actions.Children.Add(Theme.Button("Remove rule",async(_,_)=>await SaveSectionAsync(section,getters,"DELETE")));
        stack.Children.Add(actions);return Theme.Card(stack);
    }
    void MarkDirty(object sender,RoutedEventArgs e){if(!rendering)dirty=true;}
    async Task SaveSectionAsync(SettingSection section,Dictionary<string,Func<string>> getters,string operation)
    {
        if(client is null || busy || testMode)return;SetBusy(true);status.Text="Checking the current settings before saving…";
        try
        {
            var edits=operation=="DELETE"?new Dictionary<string,string>():getters.ToDictionary(kv=>kv.Key,kv=>kv.Value());
            var plan=await client.PrepareAsync(section,edits,operation,cancellation.Token);
            if(MessageBox.Show(this,plan.Preview+"\n\nApply this change to the selected router?","Review settings change",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes){status.Text="Save cancelled; no update was sent.";return;}
            status.Text="Saving the reviewed change…";var message=await client.ApplyAsync(plan,cancellation.Token);dirty=false;status.Text=message;
            MessageBox.Show(this,message,"Save result",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex){status.Text=FriendlyError(ex);MessageBox.Show(this,status.Text,"Setting not verified",MessageBoxButton.OK,MessageBoxImage.Information);}
        finally{SetBusy(false);}
        if(!dirty)await RefreshAsync(false);
    }
    void ExportCoverage()
    {
        try
        {
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArrisControl","Reports");Directory.CreateDirectory(directory);
            var file=Path.Combine(directory,$"coverage-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            var report=new{ApplicationVersion=ReleaseUpdater.CurrentVersion.ToString(),RawValuesSaved=false,Pages=snapshots.Values.Select(s=>new{s.Page.Id,s.Time,s.Reads,Fields=s.Rows.Select(r=>new{r.Field,r.Source}).Distinct()})};
            File.WriteAllText(file,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));status.Text="Coverage report saved: "+file;
        }
        catch(IOException){status.Text="Could not save the coverage report.";}
    }
    async Task CheckUpdatesAsync()
    {
        if(busy || !DiscardEdits())return;SetBusy(true);status.Text="Checking GitHub releases…";
        try
        {
            var release=await ReleaseUpdater.CheckAsync(cancellation.Token);
            if(release is null){status.Text="This version is up to date. Local router access is unchanged.";return;}
            if(MessageBox.Show(this,$"Version {release.Version} is available from HoodNewsSanaa/Arris-Control. Download, verify, and restart the app?","Application update",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes){status.Text="Update available; installation deferred.";return;}
            status.Text="Downloading and verifying the application update…";var staged=await ReleaseUpdater.DownloadAsync(release,cancellation.Token);
            ReleaseUpdater.StartApply(staged);dirty=false;Close();
        }
        catch(Exception ex){status.Text=ex is RouterException?ex.Message:"Could not check or download an update. The current app remains usable.";}
        finally{SetBusy(false);}
    }
    public void LoadFixture()
    {
        page=Catalog.Pages.First(p=>p.Id=="internet");title.Text=page.Name;description.Text=page.Description;
        changingSelection=true;nav.SelectedItem=page;changingSelection=false;status.Text="Example data · No router requests";
        var data=new Dictionary<string,JsonObject>{["NETWORKDNS_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"NETWORKDNS\":{\"DYNAMICV4\":\"0\",\"DNSIPV4SERVERS\":\"8.8.8.8,1.1.1.1\",\"DYNAMICV6\":\"1\",\"DNSIPV6SERVERS\":\"0.0.0.0,0.0.0.0\"}}")!.AsObject(),["DEVICEOPERMODE_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"DEVICEOPERMODE\":{\"MODE\":\"DUAL\"}}")!.AsObject()};
        current=new(page,data,[new("NETWORKDNS_ALL",200,"OK",DateTimeOffset.Now,true)],data.SelectMany(kv=>Catalog.Flatten(kv.Value,kv.Key)).ToList(),DateTimeOffset.Now);snapshots[page.Id]=current;Render(current);
    }
    public void VerifyFixture(){if(content.Children.Count<2 || fullGrid.Items.Count<5 || tabs.Items.Count!=2)throw new InvalidOperationException("Native UI binding failed.");}
}
