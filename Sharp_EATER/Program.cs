using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SharpRES
{
    class Program
    {
        // Helper class for deserializing RESList.json
        private class ResListEntry
        {
            public int Index { get; set; }
            public string ResFilePath { get; set; }
            public string JsonFilePath { get; set; }
        }

        static void Main(string[] args)
        {
            // Validate command-line arguments
            if (args.Length < 2)
            {
                Console.WriteLine("Sharp Eater by Yamato Nagasaki [Experimental Release v1.4]\n- A GOD EATER Tool. Used for basic single RES/RTBL Unpacking/Repacking\n\n");
                Console.WriteLine("Usage for Unpacking: Sharp_EATER.exe -x [input.res|input.rtbl]" +
                                    "\n--> When unpacking, it will generate dictionaries and a JSON file counterpart of the input file\n");
                Console.WriteLine("Usage for Repacking: Sharp_EATER.exe -r [input.res] [input.json] [-E]" +
                                    "\n--> When repacking, always specify the JSON file counterpart of the input RES file" +
                                    "\n--> Use -E to enforce Package/Data/Patch files into RES file with SET_C/SET_D masking. use this when needed to.\n");
                Console.WriteLine("Usage for Nested Repacking: Sharp_EATER.exe -r [parent.res] -n [RESList.json] [-E]" +
                                    "\n--> Repacks nested RES files first, then repacks the parent RES file.");
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

                        // Serialize to JSON after all extraction (including nested) is complete
                        string jsonOutput = resFile.Serialize();
                        string outputJsonFile = Path.ChangeExtension(inputFile, ".json");
                        File.WriteAllText(outputJsonFile, jsonOutput);
                        Console.WriteLine($"\nParent RES serialization complete. Output saved to {outputJsonFile}");
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
                        Console.WriteLine("Error: Repack Modes requires the following arguments.");
                        Console.WriteLine("--> Usage (Normal Repacking): Sharp_EATER.exe -r [input.res] [input.json]");
                        Console.WriteLine("--> Usage (Nested Repacking): Sharp_EATER.exe -r [parent.res] -n [RESList.json]");
                        return;
                    }

                    if (args[2].ToLower() == "-n")
                    {
                        // Nested Repack Mode
                        if (args.Length < 4)
                        {
                            Console.WriteLine("Error: -n mode requires a RESList.json's file path.");
                            return;
                        }
                        string resListFile = args[3];
                        bool enforcedInput = args.Length > 4 && args[4].ToLower() == "-e";
                        HandleNestedRepack(inputFile, resListFile, enforcedInput);
                    }
                    else
                    {
                        // Standard Repack Mode
                        string inputJsonFile = args[2];
                        bool enforcedInput = args.Length > 3 && args[3].ToLower() == "-e";
                        PackRES packer = new PackRES(inputFile, inputJsonFile, enforcedInput);
                        packer.Repack();
                    }
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

        private static void HandleNestedRepack(string parentResFile, string resListFile, bool enforcedInput)
        {
            if (!File.Exists(resListFile))
            {
                Console.WriteLine($"Error: RESList file not found: {resListFile}");
                return;
            }

            Console.WriteLine($"=== Starting Nested Repack process from: {resListFile} ===");

            // 1. Read and process nested RES files in reverse order
            string jsonContent = File.ReadAllText(resListFile);
            var resList = JsonSerializer.Deserialize<List<ResListEntry>>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resList == null || resList.Count == 0)
            {
                Console.WriteLine("Warning: RESList.json is empty or invalid. No nested files to repack.");
            }
            else
            {
                var reversedList = resList.OrderByDescending(e => e.Index).ToList();
                int total = reversedList.Count;
                int current = 0;

                foreach (var entry in reversedList)
                {
                    current++;
                    Console.WriteLine($"\n--- Repacking Nested File {current} of {total}: {entry.ResFilePath} ---");
                    try
                    {
                        // For nested files, enforced mode is typically not needed, but we pass the flag along just in case.
                        PackRES nestedPacker = new PackRES(entry.ResFilePath, entry.JsonFilePath, enforcedInput);
                        nestedPacker.Repack();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error repacking nested file {entry.ResFilePath}: {ex.Message}");
                        Console.WriteLine("Aborting nested repack process.");
                        return;
                    }
                }
            }

            // 2. Repack the parent RES file
            Console.WriteLine($"\n=== All nested files repacked. Repacking Parent RES File: {parentResFile} ===");
            string parentJsonFile = Path.ChangeExtension(parentResFile, ".json");
            if (!File.Exists(parentJsonFile))
            {
                Console.WriteLine($"Error: Parent JSON file not found: {parentJsonFile}");
                Console.WriteLine("Cannot repack parent file. Process stopped.");
                return;
            }

            try
            {
                PackRES parentPacker = new PackRES(parentResFile, parentJsonFile, enforcedInput);
                parentPacker.Repack();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking parent file {parentResFile}: {ex.Message}");
            }
        }
    }
}