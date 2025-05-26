using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SharpRES
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

        // Structure for RDP dictionary entries
        private class RDPDictionaryEntry
        {
            public int Index { get; set; }
            public string[] Files { get; set; }
            public uint RealOffset { get; set; }
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

                // Load RDP dictionaries
                var rdpDictionaries = LoadRDPDictionaries();

                // Initialize expandable MemoryStream for .res file
                byte[] originalResData = File.ReadAllBytes(_inputResFile);
                using (MemoryStream outputStream = new MemoryStream())
                {
                    // Determine the maximum offset needed for preserved SET_C/SET_D filesets
                    uint maxPreservedOffset = _jsonData.Configs;
                    for (int i = 0; i < _jsonData.Filesets.Count; i++)
                    {
                        var jsonFileset = _jsonData.Filesets[i];
                        var fileset = _resFile.Filesets[i];
                        if ((fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D") &&
                            jsonFileset.FilesetPointers.Any(p => !p))
                        {
                            uint endOffset = fileset.RealOffset + fileset.Size;
                            maxPreservedOffset = Math.Max(maxPreservedOffset, endOffset);
                        }
                    }

                    // Copy original data up to maxPreservedOffset to preserve non-replaced chunks
                    outputStream.Write(originalResData, 0, Math.Min(originalResData.Length, (int)maxPreservedOffset));
                    // Set length to maxPreservedOffset to ensure no residual data beyond
                    outputStream.SetLength(maxPreservedOffset);
                    using (BinaryWriter writer = new BinaryWriter(outputStream))
                    {
                        // Write and update header (32 bytes) at the beginning of the file
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

                        // Prepare RDP file streams
                        Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> rdpStreams = PrepareRDPStreams();

                        // Track Filesets that share realOffset for RDP files
                        Dictionary<string, Dictionary<uint, (uint Size, uint UnpackSize)>> rdpSharedOffsets = new Dictionary<string, Dictionary<uint, (uint, uint)>>();
                        foreach (var rdp in rdpStreams.Keys)
                            rdpSharedOffsets[rdp] = new Dictionary<uint, (uint, uint)>();

                        // Track new offsets for SET_C/SET_D Filesets
                        Dictionary<int, (uint RealOffset, uint RawOffset)> newOffsets = new Dictionary<int, (uint, uint)>();

                        // Replace chunks for SET_C, SET_D, Package, Data, and Patch Filesets
                        uint currentResOffset = _jsonData.Configs; // Start at Configs offset for .res file
                        for (int i = 0; i < _resFile.Filesets.Count; i++)
                        {
                            var fileset = _resFile.Filesets[i];
                            var jsonFileset = _jsonData.Filesets[i];

                            // Skip if DataSet or any FilesetPointers are invalid/false
                            if (fileset.AddressMode == "DataSet")
                            {
                                Console.WriteLine($"Fileset {i + 1}: [AddressMode=DataSet] - Skipped.");
                                continue;
                            }
                            if (jsonFileset.FilesetPointers.Any(p => !p))
                            {
                                Console.WriteLine($"Fileset {i + 1}: [Invalid FilesetPointers] - Skipped replacement, preserved at original offset 0x{fileset.RealOffset:X8}.");
                                newOffsets[i] = (fileset.RealOffset, fileset.RawOffset);
                                continue;
                            }

                            // RDP or RES file handling
                            string rdpFile = null;
                            BinaryWriter targetWriter = writer;
                            uint currentOffset = currentResOffset;
                            bool isRDP = false;
                            switch (fileset.AddressMode)
                            {
                                case "SET_C":
                                case "SET_D":
                                    // Handled in .res file
                                    break;
                                case "Package":
                                    rdpFile = "package.rdp";
                                    if (!rdpStreams.ContainsKey(rdpFile))
                                    {
                                        Console.WriteLine($"Fileset {i + 1}: [Package] - Missing package.rdp or dictionary, skipped.");
                                        continue;
                                    }
                                    isRDP = true;
                                    targetWriter = rdpStreams[rdpFile].Writer;
                                    currentOffset = fileset.RealOffset;
                                    break;
                                case "Data":
                                    rdpFile = "data.rdp";
                                    if (!rdpStreams.ContainsKey(rdpFile))
                                    {
                                        Console.WriteLine($"Fileset {i + 1}: [Data] - Missing data.rdp or dictionary, skipped.");
                                        continue;
                                    }
                                    isRDP = true;
                                    targetWriter = rdpStreams[rdpFile].Writer;
                                    currentOffset = fileset.RealOffset;
                                    break;
                                case "Patch":
                                    rdpFile = "patch.rdp";
                                    if (!rdpStreams.ContainsKey(rdpFile))
                                    {
                                        Console.WriteLine($"Fileset {i + 1}: [Patch] - Missing patch.rdp or dictionary, skipped.");
                                        continue;
                                    }
                                    isRDP = true;
                                    targetWriter = rdpStreams[rdpFile].Writer;
                                    currentOffset = fileset.RealOffset;
                                    break;
                                default:
                                    Console.WriteLine($"Fileset {i + 1}: [AddressMode={fileset.AddressMode}] - Skipped.");
                                    continue;
                            }

                            // Read file from filename
                            string filename = jsonFileset.Filename;
                            if (string.IsNullOrEmpty(filename) || !File.Exists(filename))
                            {
                                Console.WriteLine($"Fileset {i + 1}: [Missing or invalid filename: {filename}] - Skipped.");
                                newOffsets[i] = (fileset.RealOffset, fileset.RawOffset);
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

                                if (isRDP)
                                {
                                    // Handle RDP chunk replacement
                                    uint originalSize = fileset.Size;
                                    uint originalEOF = fileset.RealOffset + originalSize;

                                    if (newSize <= originalSize)
                                    {
                                        // Smaller or equal size: write chunk and pad with zeros to original EOF
                                        targetWriter.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
                                        targetWriter.Write(newChunk);
                                        if (newSize < originalSize)
                                        {
                                            targetWriter.Write(new byte[originalSize - newSize]);
                                        }
                                    }
                                    else
                                    {
                                        // Larger size: check for zero-padding after original EOF
                                        uint requiredSpace = newSize - originalSize;
                                        bool canFit = CheckZeroPadding(rdpFile, originalEOF, requiredSpace);
                                        if (!canFit)
                                        {
                                            Console.WriteLine($"Fileset {i + 1}: [RDP={rdpFile}] - New size {newSize} exceeds available space, skipped.");
                                            continue;
                                        }

                                        // Write new chunk
                                        targetWriter.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
                                        targetWriter.Write(newChunk);
                                    }
                                }
                                else
                                {
                                    // Ensure MemoryStream can accommodate new chunk
                                    long requiredCapacity = currentOffset + newSize;
                                    if (requiredCapacity > outputStream.Capacity)
                                    {
                                        outputStream.Capacity = (int)Math.Max(outputStream.Capacity * 2, requiredCapacity);
                                    }

                                    // Write chunk to .res file
                                    outputStream.Seek(currentOffset, SeekOrigin.Begin);
                                    targetWriter.Write(newChunk);
                                }

                                // Update Fileset properties
                                fileset.Size = newSize;
                                fileset.UnpackSize = newUnpackSize;
                                if (!isRDP)
                                {
                                    // Update offsets for .res file (SET_C/SET_D)
                                    fileset.RealOffset = currentOffset;
                                    fileset.RawOffset = (fileset.AddressMode == "SET_C" ? SET_C_MASK : SET_D_MASK) | (currentOffset & 0x00FFFFFF);
                                    newOffsets[i] = (fileset.RealOffset, fileset.RawOffset);
                                }

                                // Store size for shared realOffset in RDP files
                                if (isRDP)
                                {
                                    rdpSharedOffsets[rdpFile][fileset.RealOffset] = (newSize, newUnpackSize);
                                }

                                // Update Fileset in .res output stream
                                long filesetOffset = 0x60 + (i * 32);
                                outputStream.Seek(filesetOffset, SeekOrigin.Begin);
                                writer.Write(fileset.RawOffset); // 4 bytes
                                writer.Write(fileset.Size); // 4 bytes
                                writer.Write(fileset.OffsetName); // 4 bytes
                                writer.Write(fileset.ChunkName); // 4 bytes
                                writer.Write(new byte[12]); // 12 bytes padding
                                writer.Write(fileset.UnpackSize); // 4 bytes

                                // Log replacement
                                Console.WriteLine($"Fileset {i + 1}: Replaced chunk at 0x{currentOffset:X8} in {(isRDP ? rdpFile : ".res file")}, Size={newSize} bytes, UnpackSize={newUnpackSize} bytes, Compressed={isCompressed}, File={filename}");

                                // Update offset for .res file
                                if (!isRDP)
                                {
                                    currentResOffset += newSize;
                                    uint padding = (16 - (currentResOffset % 16)) % 16;
                                    if (padding > 0)
                                    {
                                        // Ensure capacity for padding
                                        if (currentResOffset + padding > outputStream.Capacity)
                                        {
                                            outputStream.Capacity = (int)(currentResOffset + padding);
                                        }
                                        outputStream.Seek(currentResOffset, SeekOrigin.Begin);
                                        writer.Write(new byte[padding]);
                                        currentResOffset += padding;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Fileset {i + 1}: Failed to process {filename}. Error: {ex.Message}");
                                newOffsets[i] = (fileset.RealOffset, fileset.RawOffset);
                                continue;
                            }
                        }

                        // Update Filesets information in RDP filesets
                        foreach (var rdp in rdpSharedOffsets.Keys)
                        {
                            foreach (var kvp in rdpSharedOffsets[rdp])
                            {
                                uint realOffset = kvp.Key;
                                var (size, unpackSize) = kvp.Value;
                                for (int i = 0; i < _resFile.Filesets.Count; i++)
                                {
                                    var fileset = _resFile.Filesets[i];
                                    if ((fileset.AddressMode == "Package" && rdp == "package.rdp" ||
                                         fileset.AddressMode == "Data" && rdp == "data.rdp" ||
                                         fileset.AddressMode == "Patch" && rdp == "patch.rdp") &&
                                        fileset.RealOffset == realOffset)
                                    {
                                        fileset.Size = size;
                                        fileset.UnpackSize = unpackSize;
                                        long filesetOffset = 0x60 + (i * 32);
                                        outputStream.Seek(filesetOffset, SeekOrigin.Begin);
                                        writer.Write(fileset.RawOffset); // 4 bytes
                                        writer.Write(fileset.Size); // 4 bytes
                                        writer.Write(fileset.OffsetName); // 4 bytes
                                        writer.Write(fileset.ChunkName); // 4 bytes
                                        writer.Write(new byte[12]); // 12 bytes padding
                                        writer.Write(fileset.UnpackSize); // 4 bytes
                                        Console.WriteLine($"Fileset {i + 1}: Updated shared realOffset 0x{realOffset:X8} in {rdp}, Size={size}, UnpackSize={unpackSize}");
                                    }
                                }
                            }
                        }

                        // Copy remaining data for non-repacked Filesets, only IF FilesetPointers are valid
                        for (int i = 0; i < _resFile.Filesets.Count; i++)
                        {
                            var fileset = _resFile.Filesets[i];
                            var jsonFileset = _jsonData.Filesets[i];
                            if ((fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D") &&
                                !newOffsets.ContainsKey(i) &&
                                !jsonFileset.FilesetPointers.Any(p => !p))
                            {
                                // Copy original chunk to new offset
                                uint originalOffset = fileset.RealOffset;
                                uint size = fileset.Size;
                                if (size == 0)
                                    continue;

                                // Ensure capacity
                                if (currentResOffset + size > outputStream.Capacity)
                                {
                                    outputStream.Capacity = (int)(currentResOffset + size);
                                }

                                // Read original chunk
                                byte[] chunk = new byte[size];
                                Array.Copy(originalResData, originalOffset, chunk, 0, size);

                                // Write to new offset
                                outputStream.Seek(currentResOffset, SeekOrigin.Begin);
                                writer.Write(chunk);

                                // Update Fileset properties
                                fileset.RealOffset = currentResOffset;
                                fileset.RawOffset = (fileset.AddressMode == "SET_C" ? SET_C_MASK : SET_D_MASK) | (currentResOffset & 0x00FFFFFF);
                                newOffsets[i] = (fileset.RealOffset, fileset.RawOffset);

                                // Update Fileset in .res output stream
                                long filesetOffset = 0x60 + (i * 32);
                                outputStream.Seek(filesetOffset, SeekOrigin.Begin);
                                writer.Write(fileset.RawOffset); // 4 bytes
                                writer.Write(fileset.Size); // 4 bytes
                                writer.Write(fileset.OffsetName); // 4 bytes
                                writer.Write(fileset.ChunkName); // 4 bytes
                                writer.Write(new byte[12]); // 12 bytes padding
                                writer.Write(fileset.UnpackSize); // 4 bytes

                                Console.WriteLine($"Fileset {i + 1}: Copied original chunk to 0x{currentResOffset:X8}, Size={size} bytes");

                                // Update offset with padding
                                currentResOffset += size;
                                uint padding = (16 - (currentResOffset % 16)) % 16;
                                if (padding > 0)
                                {
                                    if (currentResOffset + padding > outputStream.Capacity)
                                    {
                                        outputStream.Capacity = (int)(currentResOffset + padding);
                                    }
                                    outputStream.Seek(currentResOffset, SeekOrigin.Begin);
                                    writer.Write(new byte[padding]);
                                    currentResOffset += padding;
                                }
                            }
                            else if ((fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D") &&
                                     jsonFileset.FilesetPointers.Any(p => !p))
                            {
                                // Preserve at original offset, already handled by initial copy
                                Console.WriteLine($"Fileset {i + 1}: Preserved original chunk at 0x{fileset.RealOffset:X8}, Size={fileset.Size} bytes");
                            }
                        }

                        // Close RDP streams
                        foreach (var rdp in rdpStreams.Values)
                        {
                            rdp.Writer.Dispose();
                            rdp.Stream.Dispose();
                            Console.WriteLine($"Repacked RDP file saved to {rdp.OutputPath}");
                        }

                        // Writing the output file
                        string outputResFile = Path.Combine(
                            Path.GetDirectoryName(_inputResFile) ?? string.Empty,
                            Path.GetFileNameWithoutExtension(_inputResFile) + "_repacked.res"
                        );
                        File.WriteAllBytes(outputResFile, outputStream.ToArray());
                        Console.WriteLine($"Repacking complete. Output saved to {outputResFile}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking files: {ex.Message}");
                throw;
            }
        }

        private Dictionary<string, List<RDPDictionaryEntry>> LoadRDPDictionaries()
        {
            // Loads RDP dictionary files (packageDict.json, dataDict.json, patchDict.json)
            var dictionaries = new Dictionary<string, List<RDPDictionaryEntry>>();
            string[] rdpFiles = { "packageDict.json", "dataDict.json", "patchDict.json" };
            string[] rdpNames = { "package.rdp", "data.rdp", "patch.rdp" };

            for (int i = 0; i < rdpFiles.Length; i++)
            {
                string jsonPath = rdpFiles[i];
                string rdpName = rdpNames[i];
                if (!File.Exists(jsonPath))
                {
                    Console.WriteLine($"Dictionary {jsonPath} not found, skipping {rdpName}.");
                    continue;
                }

                try
                {
                    string jsonContent = File.ReadAllText(jsonPath);
                    var entries = JsonSerializer.Deserialize<List<RDPDictionaryEntry>>(jsonContent, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    if (entries == null || entries.Count == 0)
                    {
                        Console.WriteLine($"Dictionary {jsonPath} is empty, skipping {rdpName}.");
                        continue;
                    }

                    dictionaries[rdpName] = entries.OrderBy(e => e.Index).ToList();
                    Console.WriteLine($"Loaded dictionary {jsonPath} with {entries.Count} entries for {rdpName}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load dictionary {jsonPath}: {ex.Message}, skipping {rdpName}.");
                }
            }

            return dictionaries;
        }

        private Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> PrepareRDPStreams()
        {
            // Prepares file streams for RDP files (package.rdp, data.rdp, patch.rdp)
            var streams = new Dictionary<string, (FileStream, BinaryWriter, string)>();
            string[] rdpFiles = { "package.rdp", "data.rdp", "patch.rdp" };

            foreach (var rdpFile in rdpFiles)
            {
                if (!File.Exists(rdpFile))
                {
                    Console.WriteLine($"RDP file {rdpFile} not found, skipping.");
                    continue;
                }

                string outputRdpFile = Path.Combine(
                    Path.GetDirectoryName(rdpFile) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(rdpFile) + "_new.rdp"
                );

                try
                {
                    // Copy original RDP file to new file
                    File.Copy(rdpFile, outputRdpFile, true);
                    var stream = new FileStream(outputRdpFile, FileMode.Open, FileAccess.ReadWrite);
                    var writer = new BinaryWriter(stream);
                    streams[rdpFile] = (stream, writer, outputRdpFile);
                    Console.WriteLine($"Prepared RDP stream for {outputRdpFile}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to prepare RDP stream for {outputRdpFile}: {ex.Message}, skipping.");
                }
            }

            return streams;
        }

        private bool CheckZeroPadding(string rdpFile, uint startOffset, uint requiredSpace)
        {
            // Checks for sufficient zero padding in RDP files for larger chunks
            try
            {
                using (var stream = new FileStream(rdpFile, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Seek(startOffset, SeekOrigin.Begin);
                    uint zerosFound = 0;

                    while (zerosFound < requiredSpace && stream.Position < stream.Length)
                    {
                        byte b = reader.ReadByte();
                        if (b != 0)
                            return false;
                        zerosFound++;
                    }

                    return zerosFound >= requiredSpace;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking zero padding in {rdpFile} at 0x{startOffset:X8}: {ex.Message}");
                return false;
            }
        }

        private void Load()
        {
            // Validates and loads input .res and .json files
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
            // Validates consistency between .res file and JSON data
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
}