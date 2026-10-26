using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ArrisControl;
internal sealed class AcquireWindow : Window
{
    readonly TextBox email=new(),address=new(){Text="192.168.7.1"};readonly PasswordBox password=new(){Padding=new Thickness(9,7,9,7)};
    readonly ComboBox devices=new(){MinWidth=470};readonly TextBlock status=Theme.Text("Sign in to retrieve the account's existing router credential.",14,Theme.Muted);
    readonly TextBlock reference=Theme.Text("Protocol reference will be detected locally or selected from an APK/XAPK.",12,Theme.Muted);
    readonly StackPanel body=new(){Margin=new Thickness(25)};readonly CancellationTokenSource cancel=new();
    readonly Button signIn,verify,browse,restore;CloudConfiguration? configuration;AcquiredCredential? acquired;bool busy;
    public CredentialRecord? SavedRecord {get;private set;}
    public AcquireWindow()
    {
        Title="Acquire local router credential";Width=740;Height=720;MinWidth=650;MinHeight=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=new System.Windows.Media.FontFamily("Segoe UI");FontSize=14;Background=System.Windows.Media.Brushes.White;
        Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        body.Children.Add(Theme.Text("Acquire your router credential",24));
        var explanation=Theme.Text("Sign in to your ARRIS account, select a registered router, and verify its local connection. Every acquired credential is saved encrypted; verified credentials can then be used without cloud sign-in. No router settings or account passwords are changed.",14,Theme.Muted);explanation.Margin=new Thickness(0,10,0,17);body.Children.Add(explanation);
        AddField("ARRIS account email",email);AddField("Current ARRIS cloud-account password",password);
        var protocol=new StackPanel{Margin=new Thickness(0,12,0,15)};protocol.Children.Add(reference);browse=Theme.Button("Select protocol APK/XAPK…",async(_,_)=>await BrowseAsync());browse.Margin=new Thickness(0,7,0,0);protocol.Children.Add(browse);body.Children.Add(protocol);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};signIn=Theme.Button("Sign in and acquire",async(_,_)=>await SignInAsync(),true);restore=Theme.Button("Load saved acquisition…",(_,_)=>Restore());actions.Children.Add(signIn);actions.Children.Add(restore);body.Children.Add(actions);
        var separator=new Separator{Margin=new Thickness(0,19,0,13)};body.Children.Add(separator);
        AddField("Registered router",devices);AddField("Router local IPv4 address",address);
        verify=Theme.Button("Verify locally and save credential",async(_,_)=>await VerifyAsync(),true);verify.Margin=new Thickness(0,15,0,12);verify.IsEnabled=false;body.Children.Add(verify);
        status.Margin=new Thickness(0,8,0,0);body.Children.Add(status);
        Closing+=(_,e)=>{if(busy){e.Cancel=true;status.Text="Wait for the current bounded request to finish before closing.";}};
        Closed+=(_,_)=>{cancel.Cancel();cancel.Dispose();password.Clear();acquired=null;configuration=null;};
    }
    void AddField(string label,Control control){var text=Theme.Text(label,13);text.Margin=new Thickness(0,8,0,5);body.Children.Add(text);body.Children.Add(control);}
    void SetBusy(bool value){busy=value;signIn.IsEnabled=!value;restore.IsEnabled=!value;browse.IsEnabled=!value;email.IsEnabled=!value;password.IsEnabled=!value;address.IsEnabled=!value;devices.IsEnabled=!value;verify.IsEnabled=!value && acquired is not null && acquired.Devices.Count>0;}
    async Task BrowseAsync()
    {
        var picker=new OpenFileDialog{Title="Select the static SURFboard Central 4.3.5 reference",Filter="SURFboard reference (*.apk;*.xapk;BuildConfig.java)|*.apk;*.xapk;BuildConfig.java"};if(picker.ShowDialog(this)!=true)return;
        SetBusy(true);status.Text="Reading static protocol constants; nothing will be installed or executed…";
        try{configuration=await Task.Run(()=>CloudProtocol.ReadReference(picker.FileName));CloudProtocol.Save(configuration);reference.Text="Protocol reference loaded and protected for this Windows user.";status.Text="Ready for ARRIS account sign-in.";}
        catch(Exception ex){status.Text=SafeError(ex);}finally{SetBusy(false);}
    }
    async Task SignInAsync()
    {
        var account=email.Text.Trim();var cloudPassword=password.Password;SetBusy(true);acquired=null;devices.ItemsSource=null;
        try
        {
            status.Text="Loading the local protocol reference…";configuration??=await Task.Run(CloudProtocol.Discover);
            if(configuration is null)throw new RouterException("Select the SURFboard Central APK/XAPK once so the native client can read the protocol configuration. Android installation or execution is not needed.");
            reference.Text="Protocol reference ready.";status.Text="Signing in with SRP and reading existing account data…";
            using var api=new CloudApi();var result=await CloudAcquisition.AcquireAsync(account,cloudPassword,configuration,api,cancel.Token);
            var path=AcquisitionStore.Save(result);acquired=result;devices.ItemsSource=acquired.Devices;
            if(acquired.Devices.Count==1)devices.SelectedIndex=0;
            status.Text="Acquired credential saved protected in "+Path.GetDirectoryName(path)+(acquired.Devices.Count>0?". Select the router connected to this LAN and verify it below.":". The account returned no usable serial/MAC pairs, so no local login can be verified.");
        }
        catch(Exception ex){status.Text=SafeError(ex);}
        finally{password.Clear();cloudPassword="";SetBusy(false);}
    }
    void Restore()
    {
        var picker=new OpenFileDialog{Title="Select a protected acquired credential",Filter="Protected acquisition (*.json)|*.json"};if(Directory.Exists(AcquisitionStore.Folder))picker.InitialDirectory=AcquisitionStore.Folder;
        if(picker.ShowDialog(this)!=true)return;
        try{acquired=AcquisitionStore.Load(picker.FileName);devices.ItemsSource=acquired.Devices;if(acquired.Devices.Count==1)devices.SelectedIndex=0;status.Text="Saved acquisition loaded locally; no cloud request. Select the device and verify its local connection.";verify.IsEnabled=true;}
        catch(Exception ex){status.Text=SafeError(ex);}
    }
    async Task VerifyAsync()
    {
        if(acquired is null || devices.SelectedItem is not CloudDevice selected){status.Text="Select the exact registered router first.";return;}
        SetBusy(true);status.Text="Authenticating directly and verifying the router serial; no settings writes…";
        try{SavedRecord=await CloudAcquisition.VerifyAndSaveAsync(acquired,selected,address.Text.Trim(),cancel.Token);status.Text="Local authentication and exact router identity verified. Credential saved protected.";MessageBox.Show(this,status.Text,"Credential ready",MessageBoxButton.OK,MessageBoxImage.Information);busy=false;DialogResult=true;}
        catch(Exception ex){status.Text=SafeError(ex);}finally{SetBusy(false);}
    }
    static string SafeError(Exception ex)=>ex is RouterException?ex.Message:ex is OperationCanceledException?"The request timed out or was cancelled. No router settings were changed.":"Credential acquisition or local verification failed. Secret values are withheld; no settings changes were requested.";
    public void VerifyLayout()
    {
        var visual=(FrameworkElement)Content;visual.Measure(new Size(740,720));visual.Arrange(new Rect(0,0,740,720));visual.UpdateLayout();
        if(verify.IsEnabled || !signIn.IsEnabled || body.Children.Count<10)throw new InvalidOperationException("Credential acquisition UI validation failed.");
    }
}
