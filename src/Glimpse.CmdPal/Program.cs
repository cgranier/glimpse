using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer.CsWinRT;

namespace Glimpse.CmdPal;

public static class Program
{
    // Command Palette activates the package's COM server with this argument; the process lives
    // until the palette releases the extension. (Template pattern — keep as is.)
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();
            ManualResetEvent disposed = new(false);
            var extension = new GlimpseExtension(disposed);
            server.RegisterClass<GlimpseExtension, IExtension>(() => extension);
            server.Start();
            disposed.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("This is a Command Palette extension; it's started by Command Palette.");
        }
    }
}
