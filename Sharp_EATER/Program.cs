using System;
using System.IO;

namespace SharpRES
{
    class Program
    {
        static void Main(string[] args)
        {
            // Validate command-line arguments
            if (args.Length < 2)
            {
                Console.WriteLine("Sharp Eater by Yamato Nagasaki [Experimental Release v1.25]\n- A GOD EATER Tool. Used for basic single RES/RTBL Unpacking/Repacking\n\n");
                Console.WriteLine("Usage for Unpacking: RESExtractor.exe -x [input.res|input.rtbl]" +
                                    "\n--> When unpacking, it will generate dictionaries and a JSON file counterpart of the input file");
                Console.WriteLine("Usage for Repacking: RESExtractor.exe -r [input.res] [input.json] [-E]" +
                                    "\n--> When repacking, always specify the JSON file counterpart of the input RES file" +
                                    "\n--> Use -E to enforce Package/Data/Patch files into RES file with SET_C/SET_D masking");
                return;
            }

            string mode = args[0];
            string inputFile = args[1];

            try
            {
                if (mode == "-x")
                {
                    // Extraction mode
                    if (!File.Exists(inputFile))
                    {
                        Console.WriteLine($"Error: Input file not found: {inputFile}");
                        return;
                    }

                    string extension = Path.GetExtension(inputFile).ToLower();
                    if (extension == ".res")
                    {
                        bool packageRDP = File.Exists("package.rdp");
                        bool dataRDP = File.Exists("data.rdp");
                        bool patchRDP = File.Exists("patch.rdp");

                        // Read and parse the .res file
                        RES_PSP resFile;
                        using (BinaryReader reader = new BinaryReader(File.Open(inputFile, FileMode.Open)))
                        {
                            resFile = new RES_PSP(reader);
                        }

                        RESData printer = new RESData(resFile, packageRDP, dataRDP, patchRDP, inputFile);
                        printer.PrintInformation();

                        // Serialize to JSON
                        string jsonOutput = resFile.Serialize();
                        string outputJsonFile = Path.ChangeExtension(inputFile, ".json");
                        File.WriteAllText(outputJsonFile, jsonOutput);
                        Console.WriteLine($"Serialization complete. Output saved to {outputJsonFile}");
                    }
                    else if (extension == ".rtbl")
                    {
                        RTBL rtblFile = new RTBL(inputFile);
                        rtblFile.Unpack();
                    }
                    else
                    {
                        Console.WriteLine($"Error: Unsupported file extension '{extension}'. Use .res or .rtbl.");
                    }
                }
                else if (mode == "-r")
                {
                    // Repack mode
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: -r mode requires both .res and .json files.");
                        Console.WriteLine("Usage: RESExtractor.exe -r [input.res] [input.json] [-E]");
                        return;
                    }

                    string inputJsonFile = args[2];
                    bool enforcedInput = args.Length > 3 && args[3].ToLower() == "-e";
                    PackRES packer = new PackRES(inputFile, inputJsonFile, enforcedInput);
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