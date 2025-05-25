using System;
using System.IO;

namespace RESExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            // Validate command-line arguments
            if (args.Length < 2)
            {
                Console.WriteLine("Usage for Unpacking: RESExtractor.exe -x [input.res]" +
                                    "\nWhen unpacking, it will generate some dictionaries and JSON file counterpart of that input RES file");
                Console.WriteLine("Usage for Repacking: RESExtractor.exe -r [input.res] [input.json]" +
                                    "\nWhen repacking, always mention the json file counterpart of that input RES file");
                return;
            }

            string mode = args[0];
            string inputResFile = args[1];

            try
            {
                if (mode == "-x")
                {
                    // Extraction mode
                    if (!File.Exists(inputResFile))
                    {
                        Console.WriteLine($"Error: Input .res file not found: {inputResFile}");
                        return;
                    }

                    bool PackageRDP = File.Exists("package.rdp");
                    bool DataRDP = File.Exists("data.rdp");
                    bool PatchRDP = File.Exists("patch.rdp");

                    // Read and parse the .res file
                    RES_PSP resFile;
                    using (BinaryReader reader = new BinaryReader(File.Open(inputResFile, FileMode.Open)))
                    {
                        resFile = new RES_PSP(reader);
                    }

                    RESData printer = new RESData(resFile, PackageRDP, DataRDP, PatchRDP, inputResFile);
                    printer.PrintInformation();

                    // Serialize to JSON
                    string jsonOutput = resFile.Serialize();
                    string outputJsonFile = Path.ChangeExtension(inputResFile, ".json");
                    File.WriteAllText(outputJsonFile, jsonOutput);
                    Console.WriteLine($"Serialization complete. Output saved to {outputJsonFile}");
                }
                else if (mode == "-r")
                {
                    // Repack mode
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: -r mode requires both .res and .json files.");
                        Console.WriteLine("Usage: RESExtractor.exe -r [input.res] [input.json]");
                        return;
                    }

                    string inputJsonFile = args[2];
                    PackRES packer = new PackRES(inputResFile, inputJsonFile);
                    packer.Repack();
                }
                else
                {
                    Console.WriteLine($"Error: Invalid mode '{mode}'. Use -x for extraction or -r for repack.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing files: {ex.Message}");
            }
        }
    }
}