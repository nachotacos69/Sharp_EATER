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
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string mode = args[0];
            string inputFile = args[1];

            try
            {
                if (mode == "-x")
                {
                    HandleUnpack(inputFile);
                }
                else if (mode == "-r")
                {
                    HandleRepack(args);
                }
                else
                {
                    Console.WriteLine($"Error: Invalid mode '{mode}'. Use -x for extraction or -r for repack.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nAn unhandled error occurred: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void HandleUnpack(string inputFile)
        {
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

                RES_PSP resFile;
                using (BinaryReader reader = new BinaryReader(File.Open(inputFile, FileMode.Open)))
                {
                    resFile = new RES_PSP(reader);
                }

                RESData printer = new RESData(resFile, packageRDP, dataRDP, patchRDP, inputFile);
                printer.PrintInformation();

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

        private static void HandleRepack(string[] args)
        {
            string inputFile = args[1];
            EnforcementRule rule = null;
            int enforcementArgIndex = -1;

            // Find and parse enforcement rule
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("-E", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("Error: -E flag requires an enforcement rule argument (e.g., 'Package=Patch').");
                        return;
                    }
                    rule = ParseEnforcementRule(args[i + 1]);
                    if (rule == null) return;
                    enforcementArgIndex = i;
                    break;
                }
            }

            // Determine repack mode (standard, nested)
            var remainingArgs = new List<string>(args.Skip(2));
            if (enforcementArgIndex != -1)
            {
                remainingArgs.RemoveRange(enforcementArgIndex - 2, 2);
            }

            if (remainingArgs.Count == 0)
            {
                Console.WriteLine("Error: Repack mode requires additional arguments.");
                PrintRepackUsage();
                return;
            }

            Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> rdpStreams = null;
            Dictionary<string, long> rdpCursors = null;

            try
            {
                if (rule != null && rule.IsRdpToRdp)
                {
                    string targetRdpFile = GetRdpFileNameFromMode(rule.TargetMode);
                    if (!File.Exists(targetRdpFile))
                    {
                        Console.WriteLine($"Error: Cannot enforce to '{rule.TargetMode}' because the required file '{targetRdpFile}' is missing.");
                        return;
                    }
                    (rdpStreams, rdpCursors) = PrepareRDPStreams(targetRdpFile);
                }

                if (remainingArgs[0].ToLower() == "-n")
                {
                    if (remainingArgs.Count < 2)
                    {
                        Console.WriteLine("Error: -n mode requires a RESList.json file path.");
                        return;
                    }
                    string resListFile = remainingArgs[1];
                    HandleNestedRepack(inputFile, resListFile, rule, rdpStreams, rdpCursors);
                }
                else
                {
                    string inputJsonFile = remainingArgs[0];
                    PackRES packer = new PackRES(inputFile, inputJsonFile, rule, rdpStreams, rdpCursors);
                    packer.Repack();
                }
            }
            finally
            {
                if (rdpStreams != null)
                {
                    FinalizeRDPStreams(rdpStreams);
                }
            }
        }

        private static void HandleNestedRepack(string parentResFile, string resListFile, EnforcementRule rule,
                                               Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> rdpStreams,
                                               Dictionary<string, long> rdpCursors)
        {
            if (!File.Exists(resListFile))
            {
                Console.WriteLine($"Error: RESList file not found: {resListFile}");
                return;
            }

            Console.WriteLine($"=== Starting Nested Repack process from: {resListFile} ===");

            string jsonContent = File.ReadAllText(resListFile);
            var resList = JsonSerializer.Deserialize<List<ResListEntry>>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resList != null && resList.Count > 0)
            {
                var reversedList = resList.OrderByDescending(e => e.Index).ToList();
                foreach (var entry in reversedList)
                {
                    Console.WriteLine($"\n--- Repacking Nested File {entry.Index} of {resList.Count}: {entry.ResFilePath} ---");
                    try
                    {
                        PackRES nestedPacker = new PackRES(entry.ResFilePath, entry.JsonFilePath, rule, rdpStreams, rdpCursors);
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
            else
            {
                Console.WriteLine("Warning: RESList.json is empty or invalid. No nested files to repack.");
            }

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
                PackRES parentPacker = new PackRES(parentResFile, parentJsonFile, rule, rdpStreams, rdpCursors);
                parentPacker.Repack();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking parent file {parentResFile}: {ex.Message}");
            }
        }

        #region Enforcement and RDP Helpers

        private static EnforcementRule ParseEnforcementRule(string ruleString)
        {
            if (!ruleString.Contains("="))
            {
                Console.WriteLine($"Error: Invalid enforcement rule format. Expected 'Source(s)=Target'. Example: 'Package,Data=Patch'.");
                return null;
            }

            var parts = ruleString.Split('=');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                Console.WriteLine($"Error: Invalid enforcement rule format. Both source and target must be specified.");
                return null;
            }

            var rule = new EnforcementRule();
            var sourceModesRaw = parts[0].Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
            var targetModeRaw = parts[1].Trim();

            foreach (var modeStr in sourceModesRaw)
            {
                string canonicalMode = GetCanonicalAddressMode(modeStr);
                if (canonicalMode == null)
                {
                    Console.WriteLine($"Error: Invalid source address mode '{modeStr}' in enforcement rule.");
                    return null;
                }
                rule.SourceModes.Add(canonicalMode);
            }

            rule.TargetMode = GetCanonicalAddressMode(targetModeRaw);
            if (rule.TargetMode == null)
            {
                Console.WriteLine($"Error: Invalid target address mode '{targetModeRaw}' in enforcement rule.");
                return null;
            }

            rule.IsRdpToRes = rule.TargetMode == "SET_C" || rule.TargetMode == "SET_D";
            rule.IsRdpToRdp = rule.TargetMode == "Package" || rule.TargetMode == "Data" || rule.TargetMode == "Patch";

            if (!rule.IsRdpToRdp && !rule.IsRdpToRes)
            {
                Console.WriteLine($"Error: Target mode '{rule.TargetMode}' is not a valid enforcement target.");
                return null;
            }

            return rule;
        }

        private static string GetCanonicalAddressMode(string mode)
        {
            mode = mode.ToLower();
            switch (mode)
            {
                case "package": case "0x40": return "Package";
                case "data": case "0x50": return "Data";
                case "patch": case "0x60": return "Patch";
                case "set_c": case "0xc0": return "SET_C";
                case "set_d": case "0xd0": return "SET_D";
                default: return null;
            }
        }

        public static string GetRdpFileNameFromMode(string mode)
        {
            switch (mode)
            {
                case "Package": return "package.rdp";
                case "Data": return "data.rdp";
                case "Patch": return "patch.rdp";
                default: return null;
            }
        }

        public static uint GetRawOffsetFromRealOffset(uint realOffset, string addressMode)
        {
            uint mask = 0;
            switch (addressMode)
            {
                case "Package": mask = 0x40000000; break;
                case "Data": mask = 0x50000000; break;
                case "Patch": mask = 0x60000000; break;
                case "SET_C": mask = 0xC0000000; break;
                case "SET_D": mask = 0xD0000000; break;
            }

            if (mask == 0) return realOffset; // Should not happen for enforced types

            bool isRdp = addressMode == "Package" || addressMode == "Data" || addressMode == "Patch";
            uint offsetValue = isRdp ? realOffset / 0x800 : realOffset;

            return mask | (offsetValue & 0x00FFFFFF);
        }

        private static (Dictionary<string, (FileStream, BinaryWriter, string)>, Dictionary<string, long>) PrepareRDPStreams(string targetRdpFile)
        {
            var streams = new Dictionary<string, (FileStream, BinaryWriter, string)>();
            var cursors = new Dictionary<string, long>();

            string outputRdpFile = Path.Combine(Path.GetDirectoryName(targetRdpFile) ?? string.Empty, Path.GetFileNameWithoutExtension(targetRdpFile) + "_new.rdp");
            try
            {
                File.Copy(targetRdpFile, outputRdpFile, true);
                var stream = new FileStream(outputRdpFile, FileMode.Open, FileAccess.ReadWrite);
                var writer = new BinaryWriter(stream);
                streams[targetRdpFile] = (stream, writer, outputRdpFile);
                cursors[targetRdpFile] = stream.Length; // Start cursor at the end of the original file
                Console.WriteLine($"Prepared RDP stream for '{outputRdpFile}'. Original size: {stream.Length} bytes.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to prepare RDP stream for '{outputRdpFile}': {ex.Message}, skipping.");
                throw;
            }
            return (streams, cursors);
        }

        private static void FinalizeRDPStreams(Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> streams)
        {
            foreach (var pair in streams)
            {
                try
                {
                    Console.WriteLine($"Finalizing RDP file: {pair.Value.OutputPath}. New size: {pair.Value.Stream.Length} bytes.");
                    pair.Value.Writer.Close();
                    pair.Value.Stream.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error finalizing stream for {pair.Key}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Usage Info

        private static void PrintUsage()
        {
            Console.WriteLine("Sharp Eater by Yamato Nagasaki [Experimental Release v1.4]\n- A GOD EATER Tool. Used for RES Unpacking/Repacking\n");
            Console.WriteLine("Usage for Unpacking: Sharp_EATER.exe -x [input.res|input.rtbl]");
            Console.WriteLine("--> Generates dictionaries and a JSON file counterpart of the input file.\n");
            PrintRepackUsage();
        }

        private static void PrintRepackUsage()
        {
            Console.WriteLine("Usage for Repacking:");
            Console.WriteLine(" (Basic/Normal) Sharp_EATER.exe -r [input.res] [input.json] [-E rule]");
            Console.WriteLine(" (Repack with Nested) Sharp_EATER.exe -r [parent.res] -n [RESList.json] [-E rule]\n");
            Console.WriteLine("Enforcement Rule (-E) Details:");
            Console.WriteLine("  The -E flag modifies where file data is stored during repack.");
            Console.WriteLine("  Format: 'SourceMode(s)=TargetMode'");
            Console.WriteLine("  Modes can be names (Package, Data, Patch, SET_C) or hex (0x40, 0x50, etc.).\n");
            Console.WriteLine("  Example 1 (Embed in RES): Sharp_EATER.exe -r file.res file.json -E Package,Data=SET_C");
            Console.WriteLine("    --> Moves files from package.rdp and data.rdp into file.res.\n");
            Console.WriteLine("  Example 2 (Move to RDP): Sharp_EATER.exe -r file.res file.json -E Package=Patch");
            Console.WriteLine("    --> Moves files from package.rdp to the end of patch.rdp.");
        }

        #endregion
    }
}