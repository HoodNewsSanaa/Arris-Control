using System.IO;
using System.Windows;

namespace ArrisControl;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length>=2 && args[0]=="--inspect-protocol")
            {
                var config=CloudProtocol.ReadReference(args[1]);
                if(args.Length==3 && config!=CloudProtocol.ReadReference(args[2]))throw new RouterException("Static protocol references do not match.");
                Console.WriteLine("PASS: reviewed cloud protocol constants read statically; secret values withheld; no Android, network, credential access, or configuration save.");return 0;
            }
            if(args.Length==2 && args[0]=="--apply-update")return ReleaseUpdater.ApplyUpdate(args[1]);
            if(args.Contains("--self-test")){SelfTests.RunAsync().GetAwaiter().GetResult();Console.WriteLine("PASS: offline protocol, settings, privacy, update policy, and simulated save/read-back checks.");return 0;}
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};Theme.Apply(app);
            if(args.Contains("--smoke-test"))
            {
                var window=new MainWindow(true);window.LoadFixture();window.Measure(new Size(1280,850));window.Arrange(new Rect(0,0,1280,850));window.UpdateLayout();
                window.VerifyFixture();
                var index=Array.IndexOf(args,"--screenshot");
                if(index>=0 && index+1<args.Length)
                {
                    var visual=(FrameworkElement)window.Content;visual.Measure(new Size(1280,850));visual.Arrange(new Rect(0,0,1280,850));visual.UpdateLayout();
                    var image=new System.Windows.Media.Imaging.RenderTargetBitmap(1280,850,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(visual);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using var file=File.Create(args[index+1]);encoder.Save(file);
                }
                window.LoadFixture("BRIDGE");window.VerifyFixture();new AcquireWindow().VerifyLayout();
                Console.WriteLine("PASS: native WPF settings, Full settings, bypass status/off-only controls, and credential acquisition layout. No network or real credentials.");return 0;
            }
            return app.Run(new MainWindow());
        }
        catch(Exception ex)
        {
            if(args.Any(a=>a is "--self-test" or "--smoke-test" or "--inspect-protocol")){Console.Error.WriteLine(ex is RouterException ? ex.Message : ex.ToString());return 1;}
            MessageBox.Show(ex is RouterException ? ex.Message : "The application could not start. Your router settings were not changed.","Arris Control",MessageBoxButton.OK,MessageBoxImage.Error);return 1;
        }
    }
}
