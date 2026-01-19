using McpHost.Core;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace McpHost
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var gateway = new FileGateway();

                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = Encoding.UTF8;
                if (args.Length == 0) throw new ArgumentException("Comando requerido");

                switch (args[0].ToLower())
                {
                    case "read":
                        return CmdRead(gateway, args);

                    case "apply-patch":
                        return CmdApplyPatch(gateway, args);

                    default:
                        throw new ArgumentException("Comando desconocido");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        static int CmdRead(FileGateway gateway, string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("read <path>");

            var snap = gateway.Read(args[1]);
            Console.WriteLine(snap.Text);
            Console.WriteLine("-----HASH-----");
            Console.WriteLine(snap.Sha256);

            return 0;
        }

        static int CmdApplyPatch(FileGateway gateway, string[] args)
        {
            bool allowLarge = args.Contains("--large");
            if (args.Length < 4) throw new ArgumentException("apply-patch <path> <hash> <diffFile>");

            string path = args[1];
            string hash = args[2];
            string diffText = File.ReadAllText(args[3]);

            var snap = gateway.Read(path);
            gateway.ApplyPatchOnly(snap, diffText, hash, allowLarge);

            Console.WriteLine("OK");
            return 0;
        }
    }
}
