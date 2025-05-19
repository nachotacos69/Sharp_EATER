using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RESExtractor
{
    public class RESData
    {
        private readonly RES_PSP _resFile;
        private readonly bool _PackageRDP;
        private readonly bool _DataRDP;
        private readonly bool _PatchRDP;
        private readonly string _inputResFile;
        private readonly string _outputFolder;

        
        public RESData(RES_PSP resFile, bool PackageRDP, bool DataRDP, bool PatchRDP, string inputResFile)
        {
            _resFile = resFile;
            _PackageRDP = PackageRDP;
            _DataRDP = DataRDP;
            _PatchRDP = PatchRDP;
            _inputResFile = inputResFile;
            
            _outputFolder = Path.GetFileNameWithoutExtension(inputResFile);
        }

        public void PrintInformation()
        {
            // Print Header
            Console.WriteLine("=== Header ===");
            Console.WriteLine($"Magic Header: 0x{_resFile.MagicHeader:X8}");
            Console.WriteLine($"Group Offset: 0x{_resFile.GroupOffset:X8}");
            Console.WriteLine($"Group Count: {_resFile.GroupCount}");
            Console.WriteLine($"Configs Offset: 0x{_resFile.Configs:X8}");
            Console.WriteLine();

            // Print DataSets
            Console.WriteLine("=== DataSets ===");
            for (int i = 0; i < _resFile.DataSets.Count; i++)
            {
                var dataset = _resFile.DataSets[i];
                Console.WriteLine($"DataSet {i + 1}: Offset=0x{dataset.Offset:X8}, Count={dataset.Count}");
            }
            Console.WriteLine();

            // Print Filesets
            Console.WriteLine("=== Filesets ===");
            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var fileset = _resFile.Filesets[i];

                // Skip dummy or invalid filesets
                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Reserve/Dummy]");
                    continue;
                }
                // Print Reserve/Empty filesets
                
                else if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName != 0 && fileset.ChunkName != 0 && fileset.UnpackSize == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Reserve/Empty]");
                    continue;
                }

                // Check package file requirements
                bool isValid = true;
                if (fileset.AddressMode == "Package")
                    isValid = _PackageRDP; // package.rdp
                else if (fileset.AddressMode == "Data")
                    isValid = _DataRDP; // data.rdp
                else if (fileset.AddressMode == "Patch")
                    isValid = _PatchRDP; // patch.rdp

                Console.WriteLine($"Fileset {i + 1}:");
                Console.WriteLine($"  Address Mode: {fileset.AddressMode}");
                Console.WriteLine($"  Raw Offset: 0x{fileset.RawOffset:X8}");
                Console.WriteLine($"  Real Offset: 0x{fileset.RealOffset:X8}");
                Console.WriteLine($"  Size: {fileset.Size} bytes");
                Console.WriteLine($"  Unpack Size: {fileset.UnpackSize} bytes");
                Console.WriteLine($"  Offset Name: 0x{fileset.OffsetName:X8}");
                Console.WriteLine($"  Chunk Name Index: {fileset.ChunkName}");

                // Print names
                if (fileset.Names != null && fileset.Names.Length > 0)
                {
                    Console.WriteLine("  Names:");
                    for (int j = 0; j < fileset.Names.Length; j++)
                        Console.WriteLine($"    [{j}]: {fileset.Names[j]}");
                }

                if (!isValid)
                    Console.WriteLine("  [Warning]: Required package/patch file missing.");

                Console.WriteLine();
            }

            // Perform extraction after printing
            ExtractFiles();
        }

        private void ExtractFiles()
        {
            Console.WriteLine("=== Extracting Files ===");
            // Track existing files to handle duplicates
            HashSet<string> existingFiles = new HashSet<string>();

            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var fileset = _resFile.Filesets[i];

                // Skip dummy filesets
                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Dummy] - Skipped extraction.");
                    continue;
                }

                // Determine the source file based on address mode
                string sourceFile = null;
                switch (fileset.AddressMode)
                {
                    case "Package":
                        if (!_PackageRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Package] - Missing package.rdp, skipped.");
                            continue;
                        }
                        sourceFile = "package.rdp";
                        break;
                    case "Data":
                        if (!_DataRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Data] - Missing data.rdp, skipped.");
                            continue;
                        }
                        sourceFile = "data.rdp";
                        break;
                    case "Patch":
                        if (!_PatchRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Patch] - Missing patch.rdp, skipped.");
                            continue;
                        }
                        sourceFile = "patch.rdp";
                        break;
                    case "SET_C":
                    case "SET_D":
                        sourceFile = _inputResFile;
                        break;
                    case "Reserve":
                    case "Empty":
                        // Generate empty file
                        break;
                    default:
                        Console.WriteLine($"Fileset {i + 1}: [Invalid Address Mode] - Skipped extraction.");
                        continue;
                }

                // Construct output
                string outputPath = ConstructOutputPath(fileset, existingFiles, i + 1);
                if (string.IsNullOrEmpty(outputPath))
                {
                    Console.WriteLine($"Fileset {i + 1}: [Invalid Names] - Skipped extraction.");
                    continue;
                }

                try
                {
                    // Create directories
                    string directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    // Extract or create file
                    if (fileset.AddressMode == "Reserve" || fileset.AddressMode == "Empty" || fileset.Size == 0)
                    {
                        // Create empty file
                        File.WriteAllBytes(outputPath, Array.Empty<byte>());
                        Console.WriteLine($"Fileset {i + 1}: Created empty file at {outputPath}");
                    }
                    else
                    {
                        using (BinaryReader reader = new BinaryReader(File.Open(sourceFile, FileMode.Open)))
                        {
                            reader.BaseStream.Seek(fileset.RealOffset, SeekOrigin.Begin);
                            byte[] chunk = reader.ReadBytes((int)fileset.Size);
                            File.WriteAllBytes(outputPath, chunk);
                            Console.WriteLine($"Fileset {i + 1}: Extracted {fileset.Size} bytes to {outputPath}");
                        }
                    }

                    // Mark file as created
                    existingFiles.Add(outputPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fileset {i + 1}: Failed to extract to {outputPath}. Error: {ex.Message}");
                }
            }
            Console.WriteLine("=== Extraction Complete ===");
        }

        private string ConstructOutputPath(RES_PSP.Fileset fileset, HashSet<string> existingFiles, int filesetIndex)
        {
            if (fileset.Names == null || fileset.Names.Length == 0)
                return null;

            // First pointer: name, second pointer: type/extension, others: directories
            string fileName = fileset.Names[0];
            string extension = fileset.Names.Length > 1 ? fileset.Names[1] : "";
            List<string> directories = fileset.Names.Length > 2 ? fileset.Names.Skip(2).ToList() : new List<string>();

            // Prepend output folder based on input file name
            directories.Insert(0, _outputFolder);

            // Construct directory path
            string directoryPath = string.Join(Path.DirectorySeparatorChar.ToString(), directories);

            // Construct base file path
            string baseFileName = string.IsNullOrEmpty(extension) ? fileName : $"{fileName}.{extension}";
            string outputPath = string.IsNullOrEmpty(directoryPath)
                ? Path.Combine(_outputFolder, baseFileName)
                : Path.Combine(directoryPath, baseFileName);

            // Handle duplicates
            string finalPath = outputPath;
            int counter = 0;
            while (existingFiles.Contains(finalPath) || File.Exists(finalPath))
            {
                string prefix = $"_{counter:D4}";
                string newFileName = string.IsNullOrEmpty(extension)
                    ? $"{fileName}{prefix}"
                    : $"{fileName}{prefix}.{extension}";
                finalPath = string.IsNullOrEmpty(directoryPath)
                    ? Path.Combine(_outputFolder, newFileName)
                    : Path.Combine(directoryPath, newFileName);
                counter++;
            }

            return finalPath;
        }
    }
}