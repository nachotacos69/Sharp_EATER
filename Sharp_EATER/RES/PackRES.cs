using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RESExtractor
{
    public class PackRES
    {
        private readonly string _inputResFile;
        private readonly string _inputJsonFile;
        private RES_PSP _resFile;
        private JsonData _jsonData;
        private readonly uint SET_C_MASK = 0xC0000000;
        private readonly uint SET_D_MASK = 0xD0000000;

        // JSON deserialization structure
        private class JsonData
        {
            public uint MagicHeader { get; set; }
            public uint GroupOffset { get; set; }
            public byte GroupCount { get; set; }
            public uint UNK1 { get; set; }
            public uint Configs { get; set; }
            public List<JsonDataSet> DataSets { get; set; }
            public List<JsonFileset> Filesets { get; set; }
        }

        private class JsonDataSet
        {
            public uint Offset { get; set; }
            public uint Count { get; set; }
        }

        private class JsonFileset
        {
            public bool[] FilesetPointers { get; set; }
            public uint RawOffset { get; set; }
            public uint RealOffset { get; set; }
            public string AddressMode { get; set; }
            public uint Size { get; set; }
            public uint OffsetName { get; set; }
            public uint ChunkName { get; set; }
            public uint UnpackSize { get; set; }
            public uint[] NamesPointer { get; set; }
            public string[] Names { get; set; }
            public bool? Compressed { get; set; }
            public string Filename { get; set; }
        }

        public PackRES(string inputResFile, string inputJsonFile)
        {
            _inputResFile = inputResFile ?? throw new ArgumentNullException(nameof(inputResFile));
            _inputJsonFile = inputJsonFile ?? throw new ArgumentNullException(nameof(inputJsonFile));
        }

        public void Repack()
        {
            try
            {
                // Load and validate input files
                Load();

                // Copy original .res file into memory
                byte[] originalResData = File.ReadAllBytes(_inputResFile);
                using (MemoryStream outputStream = new MemoryStream(originalResData))
                using (BinaryWriter writer = new BinaryWriter(outputStream))
                {
                    // Write and Update header (32 bytes) at the beginning of the file
                    outputStream.Seek(0, SeekOrigin.Begin);
                    writer.Write(_jsonData.MagicHeader); // 4 bytes
                    writer.Write(_jsonData.GroupOffset); // 4 bytes
                    writer.Write(_jsonData.GroupCount); // 1 byte
                    writer.Write(_jsonData.UNK1); // 4 bytes
                    writer.Write(new byte[3]); // 3 bytes padding
                    writer.Write(_jsonData.Configs); // 4 bytes
                    writer.Write(new byte[12]); // 12 bytes padding

                    // Update DataSets at GroupOffset (8 bytes each, 8 groups)
                    outputStream.Seek(_jsonData.GroupOffset, SeekOrigin.Begin);
                    foreach (var jsonDataSet in _jsonData.DataSets)
                    {
                        writer.Write(jsonDataSet.Offset); // 4 bytes
                        writer.Write(jsonDataSet.Count); // 4 bytes
                    }

                    // Update Filesets starting at 0x60
                    outputStream.Seek(0x60, SeekOrigin.Begin);
                    for (int i = 0; i < _resFile.Filesets.Count; i++)
                    {
                        var fileset = _resFile.Filesets[i];
                        writer.Write(fileset.RawOffset); // 4 bytes
                        writer.Write(fileset.Size); // 4 bytes
                        writer.Write(fileset.OffsetName); // 4 bytes
                        writer.Write(fileset.ChunkName); // 4 bytes
                        writer.Write(new byte[12]); // 12 bytes padding
                        writer.Write(fileset.UnpackSize); // 4 bytes
                    }

                    // Replace chunks for SET_C and SET_D Filesets
                    uint currentOffset = _jsonData.Configs; // Start at Configs offset
                    for (int i = 0; i < _resFile.Filesets.Count; i++)
                    {
                        var fileset = _resFile.Filesets[i];
                        var jsonFileset = _jsonData.Filesets[i];

                        // Skip if not SET_C/SET_D or any FilesetPointers are false
                        if (fileset.AddressMode != "SET_C" && fileset.AddressMode != "SET_D")
                        {
                            Console.WriteLine($"Fileset {i + 1}: [AddressMode={fileset.AddressMode}] - Skipped.");
                            continue;
                        }
                        if (jsonFileset.FilesetPointers.Any(p => !p))
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Invalid FilesetPointers] - Skipped.");
                            continue;
                        }

                        // Read file from filename
                        string filename = jsonFileset.Filename;
                        if (string.IsNullOrEmpty(filename) || !File.Exists(filename))
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Missing or invalid filename: {filename}] - Skipped.");
                            continue;
                        }

                        byte[] newChunk;
                        uint newSize;
                        uint newUnpackSize;
                        bool isCompressed = jsonFileset.Compressed ?? false;

                        try
                        {
                            byte[] rawData = File.ReadAllBytes(filename);
                            newUnpackSize = (uint)rawData.Length;

                            if (isCompressed)
                            {
                                newChunk = Compression.LeCompression(rawData);
                                newSize = (uint)newChunk.Length;
                            }
                            else
                            {
                                newChunk = rawData;
                                newSize = newUnpackSize;
                            }

                            // Write chunk at currentOffset
                            outputStream.Seek(currentOffset, SeekOrigin.Begin);
                            writer.Write(newChunk);

                            // Update Fileset properties
                            fileset.Size = newSize;
                            fileset.UnpackSize = newUnpackSize;
                            fileset.RealOffset = currentOffset;
                            fileset.RawOffset = (fileset.AddressMode == "SET_C" ? SET_C_MASK : SET_D_MASK) | (currentOffset & 0x00FFFFFF);

                            // Update Fileset in output stream
                            long filesetOffset = 0x60 + (i * 32);
                            outputStream.Seek(filesetOffset, SeekOrigin.Begin);
                            writer.Write(fileset.RawOffset); // 4 bytes
                            writer.Write(fileset.Size); // 4 bytes
                            writer.Write(fileset.OffsetName); // 4 bytes
                            writer.Write(fileset.ChunkName); // 4 bytes
                            writer.Write(new byte[12]); // 12 bytes padding
                            writer.Write(fileset.UnpackSize); // 4 bytes

                            // Log replacement
                            Console.WriteLine($"Fileset {i + 1}: Replaced chunk at 0x{currentOffset:X8}, Size={newSize} bytes, UnpackSize={newUnpackSize} bytes, Compressed={isCompressed}, File={filename}");

                            // Calculate next offset with 0x10 alignment
                            currentOffset += newSize;
                            uint padding = (16 - (currentOffset % 16)) % 16;
                            if (padding > 0)
                            {
                                outputStream.Seek(currentOffset, SeekOrigin.Begin);
                                writer.Write(new byte[padding]);
                                currentOffset += padding;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Fileset {i + 1}: Failed to process {filename}. Error: {ex.Message}");
                            continue;
                        }
                    }

                    // Writing the output file
                    string outputResFile = Path.ChangeExtension(_inputResFile, "_repacked.res");
                    File.WriteAllBytes(outputResFile, outputStream.ToArray());
                    Console.WriteLine($"Repacking complete. Output saved to {outputResFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking files: {ex.Message}");
                throw;
            }
        }

        private void Load()
        {
            // Validate input files
            if (!File.Exists(_inputResFile))
                throw new FileNotFoundException($"Input .res file not found: {_inputResFile}");
            if (!File.Exists(_inputJsonFile))
                throw new FileNotFoundException($"Input .json file not found: {_inputJsonFile}");

            // Load JSON data
            string jsonContent = File.ReadAllText(_inputJsonFile);
            _jsonData = JsonSerializer.Deserialize<JsonData>(jsonContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? throw new InvalidDataException("Failed to deserialize JSON file.");

            // Load .res file
            using (BinaryReader reader = new BinaryReader(File.Open(_inputResFile, FileMode.Open)))
            {
                _resFile = new RES_PSP(reader);
            }

            // Validate data
            ValidateAndMap();
        }

        private void ValidateAndMap()
        {
            Console.WriteLine("=== Loading and Mapping RES File ===");

            // Validate header
            if (_resFile.MagicHeader != _jsonData.MagicHeader)
                throw new InvalidDataException($"MagicHeader mismatch: RES=0x{_resFile.MagicHeader:X8}, JSON=0x{_jsonData.MagicHeader:X8}");
            if (_resFile.GroupOffset != _jsonData.GroupOffset)
                throw new InvalidDataException($"GroupOffset mismatch: RES=0x{_resFile.GroupOffset:X8}, JSON=0x{_jsonData.GroupOffset:X8}");
            if (_resFile.GroupCount != _jsonData.GroupCount)
                throw new InvalidDataException($"GroupCount mismatch: RES={_resFile.GroupCount}, JSON={_jsonData.GroupCount}");
            if (_resFile.UNK1 != _jsonData.UNK1)
                throw new InvalidDataException($"UNK1 mismatch: RES=0x{_resFile.UNK1:X8}, JSON=0x{_jsonData.UNK1:X8}");
            if (_resFile.Configs != _jsonData.Configs)
                throw new InvalidDataException($"Configs mismatch: RES=0x{_resFile.Configs:X8}, JSON=0x{_jsonData.Configs:X8}");

           /* DEBUG
            Console.WriteLine("Header validated successfully:");
            Console.WriteLine($"  MagicHeader: 0x{_resFile.MagicHeader:X8}");
            Console.WriteLine($"  GroupOffset: 0x{_resFile.GroupOffset:X8}");
            Console.WriteLine($"  GroupCount: {_resFile.GroupCount}");
            Console.WriteLine($"  UNK1: 0x{_resFile.UNK1:X8}");
            Console.WriteLine($"  Configs: 0x{_resFile.Configs:X8}");
            Console.WriteLine();
           */

            // Validate DataSets
            if (_resFile.DataSets.Count != _jsonData.DataSets.Count || _resFile.DataSets.Count != 8)
                throw new InvalidDataException($"DataSets count mismatch: RES={_resFile.DataSets.Count}, JSON={_jsonData.DataSets.Count}, Expected=8");

            for (int i = 0; i < _resFile.DataSets.Count; i++)
            {
                var resDataSet = _resFile.DataSets[i];
                var jsonDataSet = _jsonData.DataSets[i];
                if (resDataSet.Offset != jsonDataSet.Offset || resDataSet.Count != jsonDataSet.Count)
                    throw new InvalidDataException($"DataSet {i + 1} mismatch: RES Offset=0x{resDataSet.Offset:X8}, Count={resDataSet.Count}; JSON Offset=0x{jsonDataSet.Offset:X8}, Count={jsonDataSet.Count}");

             // DBG   Console.WriteLine($"DataSet {i + 1}: Offset=0x{resDataSet.Offset:X8}, Count={resDataSet.Count}");
            }
            Console.WriteLine();

            // Validate Filesets
            if (_resFile.Filesets.Count != _jsonData.Filesets.Count)
                throw new InvalidDataException($"Filesets count mismatch: RES={_resFile.Filesets.Count}, JSON={_jsonData.Filesets.Count}");

            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var resFileset = _resFile.Filesets[i];
                var jsonFileset = _jsonData.Filesets[i];

                // Validate Fileset fields
                if (resFileset.RawOffset != jsonFileset.RawOffset)
                    throw new InvalidDataException($"Fileset {i + 1} RawOffset mismatch: RES=0x{resFileset.RawOffset:X8}, JSON=0x{jsonFileset.RawOffset:X8}");
                if (resFileset.Size != jsonFileset.Size)
                    throw new InvalidDataException($"Fileset {i + 1} Size mismatch: RES={resFileset.Size}, JSON={jsonFileset.Size}");
                if (resFileset.OffsetName != jsonFileset.OffsetName)
                    throw new InvalidDataException($"Fileset {i + 1} OffsetName mismatch: RES=0x{resFileset.OffsetName:X8}, JSON=0x{jsonFileset.OffsetName:X8}");
                if (resFileset.ChunkName != jsonFileset.ChunkName)
                    throw new InvalidDataException($"Fileset {i + 1} ChunkName mismatch: RES={resFileset.ChunkName}, JSON={jsonFileset.ChunkName}");
                if (resFileset.UnpackSize != jsonFileset.UnpackSize)
                    throw new InvalidDataException($"Fileset {i + 1} UnpackSize mismatch: RES={resFileset.UnpackSize}, JSON={jsonFileset.UnpackSize}");
                if (resFileset.AddressMode != jsonFileset.AddressMode)
                    throw new InvalidDataException($"Fileset {i + 1} AddressMode mismatch: RES={resFileset.AddressMode}, JSON={jsonFileset.AddressMode}");

                // Validate Names and NamesPointer
                if (resFileset.Names == null && jsonFileset.Names != null || resFileset.Names != null && jsonFileset.Names == null)
                    throw new InvalidDataException($"Fileset {i + 1} Names null mismatch");
                if (resFileset.Names != null && jsonFileset.Names != null)
                {
                    if (resFileset.Names.Length != jsonFileset.Names.Length)
                        throw new InvalidDataException($"Fileset {i + 1} Names length mismatch: RES={resFileset.Names.Length}, JSON={jsonFileset.Names.Length}");
                    for (int j = 0; j < resFileset.Names.Length; j++)
                    {
                        if (resFileset.Names[j] != jsonFileset.Names[j])
                            throw new InvalidDataException($"Fileset {i + 1} Names[{j}] mismatch: RES={resFileset.Names[j]}, JSON={jsonFileset.Names[j]}");
                    }
                }

                if (resFileset.NamesPointer == null && jsonFileset.NamesPointer != null || resFileset.NamesPointer != null && jsonFileset.NamesPointer == null)
                    throw new InvalidDataException($"Fileset {i + 1} NamesPointer null mismatch");
                if (resFileset.NamesPointer != null && jsonFileset.NamesPointer != null)
                {
                    if (resFileset.NamesPointer.Length != jsonFileset.NamesPointer.Length)
                        throw new InvalidDataException($"Fileset {i + 1} NamesPointer length mismatch: RES={resFileset.NamesPointer.Length}, JSON={jsonFileset.NamesPointer.Length}");
                    for (int j = 0; j < resFileset.NamesPointer.Length; j++)
                    {
                        if (resFileset.NamesPointer[j] != jsonFileset.NamesPointer[j])
                            throw new InvalidDataException($"Fileset {i + 1} NamesPointer[{j}] mismatch: RES=0x{resFileset.NamesPointer[j]:X8}, JSON=0x{jsonFileset.NamesPointer[j]:X8}");
                    }
                }

                // Log Fileset details
                /* DEBUG PRINTS
                Console.WriteLine($"Fileset {i + 1}:");
                Console.WriteLine($"  RawOffset: 0x{resFileset.RawOffset:X8}");
                Console.WriteLine($"  AddressMode: {resFileset.AddressMode}");
                Console.WriteLine($"  Size: {resFileset.Size} bytes");
                Console.WriteLine($"  OffsetName: 0x{resFileset.OffsetName:X8}");
                Console.WriteLine($"  ChunkName: {resFileset.ChunkName}");
                Console.WriteLine($"  UnpackSize: {resFileset.UnpackSize} bytes");
                */
                if (resFileset.Names != null && resFileset.Names.Length > 0)
                {
                    // DBG   Console.WriteLine("  Names:");
                    for (int j = 0; j < resFileset.Names.Length; j++) ;
                     // DBG   Console.WriteLine($"    [{j}]: {resFileset.Names[j]}");
                }
                if (resFileset.NamesPointer != null && resFileset.NamesPointer.Length > 0)
                {
                    // DBG Console.WriteLine("  NamesPointer:");
                    for (int j = 0; j < resFileset.NamesPointer.Length; j++) ;
                     // DBG   Console.WriteLine($"    [{j}]: 0x{resFileset.NamesPointer[j]:X8}");
                }
            }

            Console.WriteLine("=== Load and Mapping Complete ===");
        }
    }
}