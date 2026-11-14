using System;
using System.Text;

namespace EchoType.Captions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        return args switch
        {
            ["--caption-config", var mode] => CaptionConfiguration.Run(mode),
            ["--live-captions"] => LiveCaptions.Run(),
            _ => 2
        };
    }
}
