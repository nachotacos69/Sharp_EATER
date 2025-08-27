﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SharpRES
{
    public class RESData
    {
        private readonly RES_PSP _resFile;
        private readonly bool _PackageRDP;
        private readonly bool _DataRDP;
        private readonly bool _PatchRDP;
        private readonly string _inputResFile;
        private readonly string _outputFolder;
        private readonly Dictionary<uint, List<string>> _packageDict;
        private readonly Dictionary<uint, List<string>> _dataDict;
        private readonly Dictionary<uint, List<string>> _patchDict;

        public RESData(RES_PSP resFile, bool PackageRDP, bool DataRDP, bool PatchRDP, string inputResFile,
            string outputFolder = null, Dictionary<uint, List<string>> packageDict = null,
            Dictionary<uint, List<string>> dataDict = null, Dictionary<uint, List<string>> patchDict = null)
        {
            _resFile = resFile;
            _PackageRDP = PackageRDP;
            _DataRDP = DataRDP;
            _PatchRDP = PatchRDP;
            _inputResFile = inputResFile;
            _outputFolder = outputFolder ?? Path.GetFileNameWithoutExtension(inputResFile);
            _packageDict = packageDict;
            _dataDict = dataDict;
            _patchDict = patchDict;
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
                Console.WriteLine($"  Offset Name: 0x{_resFile.Filesets[i].OffsetName:X8}");
                Console.WriteLine($"  Chunk Name Index: {fileset.ChunkName}");

                // Print names
                if (fileset.Names != null && fileset.Names.Length > 0)
                {
                    Console.WriteLine("  Names:");
                    for (int j = 0; j < fileset.Names.Length; j++)
                        Console.WriteLine($"    [{j}]: {fileset.Names[j]}");
                }

                if (!isValid)
                    Console.WriteLine("  [Warning]: package/data/patch files missing.");

                Console.WriteLine();
            }

            // Perform extraction and dictionary aggregation
            ExtractFiles();
        }

        private void ExtractFiles()
        {
            Console.WriteLine("=== Extracting Files ===");
            // Track existing files to handle duplicates
            HashSet<string> existingFiles = new HashSet<string>();
            // Initialize dictionaries for standalone RES extraction
            var packageDict = _packageDict ?? new Dictionary<uint, List<string>>();
            var dataDict = _dataDict ?? new Dictionary<uint, List<string>>();
            var patchDict = _patchDict ?? new Dictionary<uint, List<string>>();
            // List to hold paths of nested RES files for subsequent extraction
            List<string> resFiles = new List<string>();

            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var fileset = _resFile.Filesets[i];

                // Skip dummy filesets
                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Dummy] - Skipped extraction.");
                    fileset.CompressedBLZ2 = null;
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                    continue;
                }

                // Determine the source file based on address mode
                string sourceFile = null;
                Dictionary<uint, List<string>> targetDict = null;
                switch (fileset.AddressMode)
                {
                    case "Package":
                        if (!_PackageRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Package] - Missing package.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "package.rdp";
                        targetDict = packageDict;
                        break;
                    case "Data":
                        if (!_DataRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Data] - Missing data.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "data.rdp";
                        targetDict = dataDict;
                        break;
                    case "Patch":
                        if (!_PatchRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Patch] - Missing patch.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "patch.rdp";
                        targetDict = patchDict;
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
                        fileset.CompressedBLZ2 = null;
                        fileset.CompressedBLZ4 = null;
                        fileset.Filename = null;
                        continue;
                }

                // Construct output path
                string outputPath = ConstructOutputPath(fileset, existingFiles, i + 1);
                if (string.IsNullOrEmpty(outputPath))
                {
                    Console.WriteLine($"Fileset {i + 1}: [Invalid Names] - Skipped extraction.");
                    fileset.CompressedBLZ2 = null;
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
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
                        fileset.CompressedBLZ2 = false;
                        fileset.CompressedBLZ4 = false;
                        fileset.Filename = outputPath;
                    }
                    else
                    {
                        byte[] chunk;
                        using (BinaryReader reader = new BinaryReader(File.Open(sourceFile, FileMode.Open)))
                        {
                            reader.BaseStream.Seek(fileset.RealOffset, SeekOrigin.Begin);
                            chunk = reader.ReadBytes((int)fileset.Size);
                        }

                        byte[] outputData;
                        bool isBLZ2 = false;
                        bool isBLZ4 = false;

                        // Check for BLZ4 compression first
                        if (BLZ4Utils.IsBLZ4(chunk))
                        {
                            outputData = BLZ4Utils.UnpackBLZ4Data(chunk);
                            isBLZ4 = true;
                        }
                        else
                        {
                            // Try BLZ2 decompression
                            outputData = Deflate.DecompressChunk(chunk, out isBLZ2);
                        }

                        // Write output data
                        File.WriteAllBytes(outputPath, outputData);

                        // Set fileset properties
                        fileset.CompressedBLZ2 = isBLZ2;
                        fileset.CompressedBLZ4 = isBLZ4;
                        fileset.Filename = outputPath;

                        // Add to RDP dictionary if applicable
                        if (targetDict != null)
                        {
                            if (!targetDict.ContainsKey(fileset.RealOffset))
                                targetDict[fileset.RealOffset] = new List<string>();
                            targetDict[fileset.RealOffset].Add(outputPath);
                        }

                        // Log extraction
                        if (isBLZ4)
                            Console.WriteLine($"Fileset {i + 1}: Decompressed BLZ4 {chunk.Length} bytes to {outputData.Length} bytes at {outputPath}");
                        else if (isBLZ2)
                            Console.WriteLine($"Fileset {i + 1}: Decompressed BLZ2 {chunk.Length} bytes to {outputData.Length} bytes at {outputPath}");
                        else
                            Console.WriteLine($"Fileset {i + 1}: Extracted {chunk.Length} bytes (raw) to {outputPath}");
                    }

                    // Track .res files for nested extraction
                    if (Path.GetExtension(outputPath).ToLower() == ".res")
                        resFiles.Add(outputPath);

                    // Mark file as created
                    existingFiles.Add(outputPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fileset {i + 1}: Failed to extract to {outputPath}. Error: {ex.Message}");
                    fileset.CompressedBLZ2 = null;
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                }
            }

            // Perform nested .res extraction, passing dictionaries for aggregation
            ExtractNestedResFiles(resFiles, packageDict, dataDict, patchDict);

            // Serialize dictionaries for standalone RES extraction after all files (including nested) are processed
            if (_packageDict == null && _dataDict == null && _patchDict == null)
            {
                SerializeRDPDictionaries(packageDict, "packageDict.json");
                SerializeRDPDictionaries(dataDict, "dataDict.json");
                SerializeRDPDictionaries(patchDict, "patchDict.json");
            }
        }

        private void ExtractNestedResFiles(List<string> resFiles, Dictionary<uint, List<string>> packageDict, Dictionary<uint, List<string>> dataDict, Dictionary<uint, List<string>> patchDict)
        {
            if (resFiles.Count == 0) return;

            Console.WriteLine($"\n=== Extracting {resFiles.Count} Nested RES Files ===");

            // Track RES files for RESList.json
            var resList = new List<object>();

            for (int i = 0; i < resFiles.Count; i++)
            {
                string resFilePath = resFiles[i];
                Console.WriteLine($"\n--- Processing nested RES file {i + 1}: {resFilePath} ---");

                try
                {
                    RES_PSP resFile;
                    using (BinaryReader reader = new BinaryReader(File.Open(resFilePath, FileMode.Open)))
                    {
                        resFile = new RES_PSP(reader);
                    }

                    string resOutputFolder = Path.Combine(Path.GetDirectoryName(resFilePath), Path.GetFileNameWithoutExtension(resFilePath));
                    RESData resData = new RESData(resFile, _PackageRDP, _DataRDP, _PatchRDP, resFilePath, resOutputFolder, packageDict, dataDict, patchDict);
                    resData.PrintInformation();

                    // Serialize nested RES file to JSON in its parent's output folder
                    string outputJsonFile = Path.ChangeExtension(resFilePath, ".json");
                    string jsonOutput = resFile.Serialize();
                    File.WriteAllText(outputJsonFile, jsonOutput);
                    Console.WriteLine($"Nested RES serialization complete. Output saved to {outputJsonFile}");

                    // Add to RESList
                    resList.Add(new
                    {
                        Index = i + 1,
                        ResFilePath = resFilePath,
                        JsonFilePath = outputJsonFile
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process nested RES file {resFilePath}: {ex.Message}");
                }
            }

            // Serialize RESList.json
            if (resList.Count > 0)
            {
                var outputDir = Path.GetDirectoryName(_inputResFile);
                var resListPath = string.IsNullOrEmpty(outputDir) ? "RESList.json" : Path.Combine(outputDir, "RESList.json");
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                string jsonOutput = JsonSerializer.Serialize(resList, options);
                if (!string.IsNullOrEmpty(Path.GetDirectoryName(resListPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(resListPath));
                File.WriteAllText(resListPath, jsonOutput);
                Console.WriteLine($"\nRESList saved to {resListPath}");
            }
        }

        private void SerializeRDPDictionaries(Dictionary<uint, List<string>> dict, string outputFileName)
        {
            // Skip serialization if dictionary is empty
            if (dict.Count == 0)
            {
                Console.WriteLine($"Skipping {outputFileName}: No entries to serialize.");
                return;
            }

            var outputDir = Path.GetDirectoryName(_inputResFile);
            var outputPath = string.IsNullOrEmpty(outputDir) ? outputFileName : Path.Combine(outputDir, outputFileName);

            var serializedData = dict.Select((kvp, index) => new
            {
                Index = index + 1,
                Files = kvp.Value,
                RealOffset = kvp.Key
            }).ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string jsonOutput = JsonSerializer.Serialize(serializedData, options);
            if (!string.IsNullOrEmpty(Path.GetDirectoryName(outputPath)))
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, jsonOutput);
            Console.WriteLine($"RDP dictionary saved to {outputPath}");
        }

        private string ConstructOutputPath(RES_PSP.Fileset fileset, HashSet<string> existingFiles, int filesetIndex)
        {
            if (fileset.Names == null || fileset.Names.Length == 0)
                return null;

            // First pointer: name, second pointer: type/extension, others: directories
            string fileName = fileset.Names[0];
            string extension = fileset.Names.Length > 1 ? fileset.Names[1] : "";
            List<string> directories = fileset.Names.Length > 2 ? fileset.Names.Skip(2).ToList() : new List<string>();

            // Prepend output folder
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
            int counter = 1;
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