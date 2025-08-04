// No Repacking for RTBL Yet...
// Sorry :(


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SharpRES
{
    public class RTBL
    {
        // Reusing Fileset structure from RES_PSP
        public class Fileset : RES_PSP.Fileset
        {
            public uint FsPos { get; set; } // Position of the fileset structure in the file
        }

        private readonly string _inputRtblFile;
        private readonly string _outputFolder;
        public List<Fileset> Filesets { get; private set; }

        public RTBL(string inputRtblFile)
        {
            _inputRtblFile = inputRtblFile ?? throw new ArgumentNullException(nameof(inputRtblFile));
            _outputFolder = Path.GetFileNameWithoutExtension(inputRtblFile);
            Filesets = new List<Fileset>();
        }

        public void Unpack()
        {
            Console.WriteLine($"=== Parsing RTBL File: {_inputRtblFile} ===");
            using (BinaryReader reader = new BinaryReader(File.Open(_inputRtblFile, FileMode.Open)))
            {
                ParseFilesets(reader);
            }
            ExtractFiles();
            Serialize();
        }

        private void ParseFilesets(BinaryReader reader)
        {
            byte[] buffer = new byte[16];
            long offset = 0;

            while (offset + 32 <= reader.BaseStream.Length)
            {
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);
                reader.Read(buffer, 0, 16);

                // Skip if first 16 bytes are all zeros
                if (buffer.All(b => b == 0))
                {
                    offset += 16;
                    continue;
                }

                // Read fileset structure (32 bytes)
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);
                Fileset fileset = new Fileset
                {
                    FsPos = (uint)offset,
                    RawOffset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    OffsetName = reader.ReadUInt32(),
                    ChunkName = reader.ReadUInt32()
                };
                reader.BaseStream.Seek(12, SeekOrigin.Current); // Skip 12 bytes padding
                fileset.UnpackSize = reader.ReadUInt32();

                // Skip if OffsetName is not 0x20
                if (fileset.OffsetName != 0x20)
                {
                    offset += 16;
                    continue;
                }

                // Process address mode and offset
                fileset.AddressMode = GetAddressMode(fileset.RawOffset);
                fileset.RealOffset = ProcessOffset(fileset.RawOffset, fileset.AddressMode);

                // Check for dummy fileset
                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0 && fileset.UnpackSize != 0)
                {
                    Console.WriteLine($"Fileset at 0x{offset:X8}: [Dummy] - Skipped.");
                    offset += 32;
                    continue;
                }

                // Read names
                if (fileset.ChunkName > 0)
                {
                    (string[] names, uint[] pointers) = ReadNames(reader, offset, fileset.ChunkName);
                    fileset.Names = names;
                    fileset.NamesPointer = pointers;
                }

                Filesets.Add(fileset);
                offset += 32;
            }

            Console.WriteLine($"Parsed {Filesets.Count} valid filesets.");
        }

        private string GetAddressMode(uint rawOffset)
        {
            byte mode = (byte)(rawOffset >> 24);
            switch (mode)
            {
                case 0x00: return "Unknown";
                case 0x30: return "DataSet";
                case 0x40: return "Package";
                case 0x50: return "Data";
                case 0x60: return "Patch";
                default: return "Invalid";
            }
        }

        private uint ProcessOffset(uint rawOffset, string addressMode)
        {
            uint offset = rawOffset & 0x00FFFFFF; 
            if (addressMode == "Package" || addressMode == "Data" || addressMode == "Patch")
                offset *= 0x800; 
            return offset;
        }

        private (string[], uint[]) ReadNames(BinaryReader reader, long offset, uint chunkName)
        {
            long originalPosition = reader.BaseStream.Position;
            // Seek to name data: offset + 32 (fileset size) + (chunkName * 4) for pointer section
            long nameOffset = offset + 32 + (chunkName * 4);
            reader.BaseStream.Seek(nameOffset, SeekOrigin.Begin);

            List<string> names = new List<string>();
            List<uint> pointers = new List<uint>();

            // Read name and extension (at least two names expected)
            for (uint i = 0; i < chunkName && reader.BaseStream.Position < reader.BaseStream.Length; i++)
            {
                uint currentOffset = (uint)reader.BaseStream.Position;
                StringBuilder sb = new StringBuilder();
                char c;
                // Read until null terminator or end of stream
                while (reader.BaseStream.Position < reader.BaseStream.Length && (c = reader.ReadChar()) != '\0')
                    sb.Append(c);
                string name = sb.ToString();
                if (!string.IsNullOrEmpty(name)) // Only add non-empty names
                    names.Add(name);
                pointers.Add(currentOffset);
            }

            reader.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            return (names.ToArray(), pointers.ToArray());
        }

        private void ExtractFiles()
        {
            Console.WriteLine("=== Extracting Files ===");
            HashSet<string> existingFiles = new HashSet<string>();
            var packageDict = new Dictionary<uint, List<string>>();
            var dataDict = new Dictionary<uint, List<string>>();
            var patchDict = new Dictionary<uint, List<string>>();

            bool packageRDP = File.Exists("package.rdp");
            bool dataRDP = File.Exists("data.rdp");
            bool patchRDP = File.Exists("patch.rdp");

            List<string> resFiles = new List<string>();

            for (int i = 0; i < Filesets.Count; i++)
            {
                var fileset = Filesets[i];

                // Skip invalid filesets
                if (fileset.AddressMode == "Unknown" || fileset.AddressMode == "DataSet")
                {
                    Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [{fileset.AddressMode}] - Skipped.");
                    fileset.CompressedBLZ2 = null;
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                    continue;
                }

                // Determine source file
                string sourceFile = null;
                Dictionary<uint, List<string>> targetDict = null;
                switch (fileset.AddressMode)
                {
                    case "Package":
                        if (!packageRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [Package] - Missing package.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "package.rdp";
                        targetDict = packageDict;
                        break;
                    case "Data":
                        if (!dataRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [Data] - Missing data.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "data.rdp";
                        targetDict = dataDict;
                        break;
                    case "Patch":
                        if (!patchRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [Patch] - Missing patch.rdp, skipped.");
                            fileset.CompressedBLZ2 = null;
                            fileset.CompressedBLZ4 = null;
                            fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "patch.rdp";
                        targetDict = patchDict;
                        break;
                    default:
                        Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [Invalid Address Mode] - Skipped.");
                        fileset.CompressedBLZ2 = null;
                        fileset.CompressedBLZ4 = null;
                        fileset.Filename = null;
                        continue;
                }

                
                string outputPath = ConstructOutputPath(fileset, existingFiles, i + 1);
                if (string.IsNullOrEmpty(outputPath))
                {
                    Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: [Invalid Names] - Skipped.");
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

                    // Extract file
                    byte[] chunk;
                    using (BinaryReader reader = new BinaryReader(File.Open(sourceFile, FileMode.Open)))
                    {
                        reader.BaseStream.Seek(fileset.RealOffset, SeekOrigin.Begin);
                        chunk = reader.ReadBytes((int)fileset.Size);
                    }

                    byte[] outputData;
                    bool isBLZ2 = false;
                    bool isBLZ4 = false;

                    if (BLZ4Utils.IsBLZ4(chunk))
                    {
                        outputData = BLZ4Utils.UnpackBLZ4Data(chunk);
                        isBLZ4 = true;
                    }
                    else
                    {
                        outputData = Deflate.DecompressChunk(chunk, out isBLZ2);
                    }

                    File.WriteAllBytes(outputPath, outputData);
                    fileset.CompressedBLZ2 = isBLZ2;
                    fileset.CompressedBLZ4 = isBLZ4;
                    fileset.Filename = outputPath;

                    // Add to RDP dictionary
                    if (targetDict != null)
                    {
                        if (!targetDict.ContainsKey(fileset.RealOffset))
                            targetDict[fileset.RealOffset] = new List<string>();
                        targetDict[fileset.RealOffset].Add(outputPath);
                    }

                    // Log extraction
                    string logMessage = isBLZ4
                        ? $"Fileset {i + 1} at 0x{fileset.FsPos:X8}: Decompressed BLZ4 {chunk.Length} bytes to {outputData.Length} bytes at {outputPath}"
                        : isBLZ2
                            ? $"Fileset {i + 1} at 0x{fileset.FsPos:X8}: Decompressed BLZ2 {chunk.Length} bytes to {outputData.Length} bytes at {outputPath}"
                            : $"Fileset {i + 1} at 0x{fileset.FsPos:X8}: Extracted {chunk.Length} bytes (raw) to {outputPath}";
                    Console.WriteLine(logMessage);

                    // Track .res files for nested extraction
                    if (Path.GetExtension(outputPath).ToLower() == ".res")
                        resFiles.Add(outputPath);

                    existingFiles.Add(outputPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fileset {i + 1} at 0x{fileset.FsPos:X8}: Failed to extract to {outputPath}. Error: {ex.Message}");
                    fileset.CompressedBLZ2 = null;
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                }
            }

            // Perform nested .res extraction, passing dictionaries for aggregation
            ExtractNestedResFiles(resFiles, packageDict, dataDict, patchDict);

            // Serialize RDP dictionaries once at the end
            SerializeRDPDictionaries(packageDict, "packageDict.json");
            SerializeRDPDictionaries(dataDict, "dataDict.json");
            SerializeRDPDictionaries(patchDict, "patchDict.json");

            Console.WriteLine("=== RTBL Extraction Complete ===");
        }

        private void ExtractNestedResFiles(List<string> resFiles, Dictionary<uint, List<string>> packageDict, Dictionary<uint, List<string>> dataDict, Dictionary<uint, List<string>> patchDict)
        {
            Console.WriteLine($"=== Extracting {resFiles.Count} Nested RES Files ===");
            bool packageRDP = File.Exists("package.rdp");
            bool dataRDP = File.Exists("data.rdp");
            bool patchRDP = File.Exists("patch.rdp");

            // Track RES files for RESList.json
            var resList = new List<object>();

            for (int i = 0; i < resFiles.Count; i++)
            {
                string resFilePath = resFiles[i];
                Console.WriteLine($"Processing nested RES file {i + 1}: {resFilePath}");

                try
                {
                    RES_PSP resFile;
                    using (BinaryReader reader = new BinaryReader(File.Open(resFilePath, FileMode.Open)))
                    {
                        resFile = new RES_PSP(reader);
                    }

                    
                    string resOutputFolder = Path.Combine(_outputFolder, Path.GetFileNameWithoutExtension(resFilePath));
                    RESData resData = new RESData(resFile, packageRDP, dataRDP, patchRDP, resFilePath, resOutputFolder, packageDict, dataDict, patchDict);
                    resData.PrintInformation();

                    // Serialize nested RES file to JSON in the parent output folder
                    string outputJsonFile = Path.Combine(_outputFolder, Path.GetFileNameWithoutExtension(resFilePath) + ".json");
                    string jsonOutput = resFile.Serialize();
                    Directory.CreateDirectory(Path.GetDirectoryName(outputJsonFile) ?? _outputFolder); 
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
                var outputDir = Path.GetDirectoryName(_inputRtblFile);
                var resListPath = string.IsNullOrEmpty(outputDir) ? "RESList.json" : Path.Combine(outputDir, "RESList.json");
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                string jsonOutput = JsonSerializer.Serialize(resList, options);
                if (!string.IsNullOrEmpty(Path.GetDirectoryName(resListPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(resListPath)); // Ensure directory exists if path includes directory
                File.WriteAllText(resListPath, jsonOutput);
                Console.WriteLine($"RESList saved to {resListPath}");
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

            var outputDir = Path.GetDirectoryName(_inputRtblFile);
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

        private string ConstructOutputPath(Fileset fileset, HashSet<string> existingFiles, int filesetIndex)
        {
            if (fileset.Names == null || fileset.Names.Length == 0)
                return null;

            string fileName = fileset.Names[0];
            string extension = fileset.Names.Length > 1 ? fileset.Names[1] : "";
            List<string> directories = fileset.Names.Length > 2 ? fileset.Names.Skip(2).ToList() : new List<string>();

            directories.Insert(0, _outputFolder);
            string directoryPath = string.Join(Path.DirectorySeparatorChar.ToString(), directories);
            string baseFileName = string.IsNullOrEmpty(extension) ? fileName : $"{fileName}.{extension}";
            string outputPath = string.IsNullOrEmpty(directoryPath)
                ? Path.Combine(_outputFolder, baseFileName)
                : Path.Combine(directoryPath, baseFileName);

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

        public string Serialize()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var serializedData = new
            {
                Filesets = Filesets.Select(fs => new
                {
                    FsPos = fs.FsPos,
                    FilesetPointers = fs.GetFilesetPointers(),
                    fs.RawOffset,
                    fs.RealOffset,
                    fs.AddressMode,
                    fs.Size,
                    fs.OffsetName,
                    fs.ChunkName,
                    fs.UnpackSize,
                    NamesPointer = fs.NamesPointer?.Select(p => (uint?)p).ToArray(),
                    fs.Names,
                    fs.CompressedBLZ2,
                    fs.CompressedBLZ4,
                    fs.Filename
                }).ToList()
            };

            string jsonOutput = JsonSerializer.Serialize(serializedData, options);
            var outputDir = Path.GetDirectoryName(_inputRtblFile);
            string outputJsonFile = string.IsNullOrEmpty(outputDir)
                ? Path.GetFileNameWithoutExtension(_inputRtblFile) + ".json"
                : Path.Combine(outputDir, Path.GetFileNameWithoutExtension(_inputRtblFile) + ".json");
            if (!string.IsNullOrEmpty(Path.GetDirectoryName(outputJsonFile)))
                Directory.CreateDirectory(Path.GetDirectoryName(outputJsonFile)); // Ensure directory exists
            File.WriteAllText(outputJsonFile, jsonOutput);
            Console.WriteLine($"RTBL serialization complete. Output saved to {outputJsonFile}");
            return jsonOutput;
        }
    }
}