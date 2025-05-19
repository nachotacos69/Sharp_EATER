using System;
using System.IO;

namespace RESExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            // Validate command-line arguments
            if (args.Length < 2 || args[0] != "-x" || !File.Exists(args[1]))
            {
                Console.WriteLine("Usage: RESExtractor.exe -x [input.res]");
                return;
            }

            string inputFile = args[1];
            bool PackageRDP = File.Exists("package.rdp");
            bool DataRDP = File.Exists("data.rdp");
            bool PatchRDP = File.Exists("patch.rdp");

            try
            {
                // Read the .res file
                using (BinaryReader reader = new BinaryReader(File.Open(inputFile, FileMode.Open)))
                {
                    // Parse the .res file structure
                    RES_PSP resFile = new RES_PSP(reader);

                    // Print the extracted information
                    RESData printer = new RESData(resFile, PackageRDP, DataRDP, PatchRDP);
                    printer.PrintInformation();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file: {ex.Message}");
            }
        }
    }
}