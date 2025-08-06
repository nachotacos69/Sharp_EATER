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
        private readonly bool _enforcedInput;
        private RES_PSP _resFile;
        private JsonData _jsonData;
        private readonly uint SET_C_MASK = 0xC0000000;
        private readonly uint SET_D_MASK = 0xD0000000;

        #region JSON_Classes
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
            public bool? CompressedBLZ2 { get; set; }
            public bool? CompressedBLZ4 { get; set; }
            public string Filename { get; set; }
        }

        private class RDPDictionaryEntry
        {
            public int Index { get; set; }
            public string[] Files { get; set; }
            public uint RealOffset { get; set; }
        }
        #endregion

        #region ContentBlock_Classes
        private abstract class ContentBlock
        {
            public int FilesetIndex { get; set; }
            public uint OriginalOffset { get; set; }
            public string BlockType { get; set; }
            public abstract uint GetSize();
        }

        private class ChunkBlock : ContentBlock
        {
            public byte[] Data { get; set; }
            public bool IsEnforced { get; set; } = false;
            public ChunkBlock() { BlockType = "Chunk"; }
            public override uint GetSize() => (uint)(Data?.Length ?? 0);
        }

        private class NameBlock : ContentBlock
        {
            public string[] Names { get; set; }
            public NameBlock() { BlockType = "Names"; }
            public override uint GetSize()
            {
                if (Names == null || Names.Length == 0) return 0;
                uint pointersSize = (uint)Names.Length * 4;
                uint stringsSize = (uint)Names.Sum(n => Encoding.Default.GetByteCount(n) + 1);
                return pointersSize + stringsSize;
            }
        }
        #endregion

        public PackRES(string inputResFile, string inputJsonFile, bool enforcedInput = false)
        {
            _inputResFile = inputResFile ?? throw new ArgumentNullException(nameof(inputResFile));
            _inputJsonFile = inputJsonFile ?? throw new ArgumentNullException(nameof(inputJsonFile));
            _enforcedInput = enforcedInput;
        }

        private uint Align16(uint offset)
        {
            return (offset + 15) & ~15u;
        }

        public void Repack()
        {
            try
            {
                Load();
                uint preferredMask = DeterminePreferredMask();
                byte[] originalResData = File.ReadAllBytes(_inputResFile);

                // --- PASS 1: STAGE ALL CONTENT BLOCKS ---
                Console.WriteLine("=== Pass 1: Staging Content Blocks ===");
                var contentBlocks = new List<ContentBlock>();
                for (int i = 0; i < _resFile.Filesets.Count; i++)
                {
                    var fileset = _resFile.Filesets[i];
                    var jsonFileset = _jsonData.Filesets[i];
                    string originalAddressMode = fileset.AddressMode;

                    bool isEnforced = _enforcedInput && (originalAddressMode == "Package" || originalAddressMode == "Data" || originalAddressMode == "Patch");
                    if (isEnforced) fileset.AddressMode = preferredMask == SET_C_MASK ? "SET_C" : "SET_D";

                    bool isSetCSD = fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D";

                    if (isSetCSD && (fileset.Size > 0 || !string.IsNullOrEmpty(jsonFileset.Filename)))
                    {
                        var chunkBlock = new ChunkBlock { FilesetIndex = i, OriginalOffset = fileset.RealOffset, IsEnforced = isEnforced };
                        bool isReplaced = !string.IsNullOrEmpty(jsonFileset.Filename) && File.Exists(jsonFileset.Filename);
                        if (isReplaced)
                        {
                            byte[] rawData = File.ReadAllBytes(jsonFileset.Filename);
                            fileset.UnpackSize = (uint)rawData.Length;
                            if (jsonFileset.CompressedBLZ2 == true) chunkBlock.Data = Compression.LeCompression(rawData);
                            else if (jsonFileset.CompressedBLZ4 == true) chunkBlock.Data = BLZ4Utils.PackBLZ4Data(rawData);
                            else chunkBlock.Data = rawData;
                            fileset.Size = (uint)chunkBlock.Data.Length;
                        }
                        else
                        {
                            chunkBlock.Data = new byte[fileset.Size];
                            Array.Copy(originalResData, (int)fileset.RealOffset, chunkBlock.Data, 0, (int)fileset.Size);
                        }
                        contentBlocks.Add(chunkBlock);
                    }

                    if (fileset.OffsetName != 0 && jsonFileset.Names != null && jsonFileset.Names.Length > 0)
                    {
                        contentBlocks.Add(new NameBlock
                        {
                            FilesetIndex = i,
                            OriginalOffset = fileset.OffsetName,
                            Names = jsonFileset.Names
                        });
                    }
                }

                contentBlocks = contentBlocks.OrderBy(b => b.OriginalOffset).ToList();
                Console.WriteLine($"Staged {contentBlocks.Count} content blocks for repacking.");

                // --- PASS 2: WRITE CONTENT AND CALCULATE NEW OFFSETS ---
                Console.WriteLine("\n=== Pass 2: Writing Content and Calculating Layout ===");
                var newChunkOffsets = new Dictionary<int, uint>();
                var newNameOffsets = new Dictionary<int, uint>();

                using (var outputStream = new MemoryStream())
                using (var writer = new BinaryWriter(outputStream))
                {
                    uint metadataEnd = (uint)(0x60 + _resFile.Filesets.Count * 32); // Determine where the metadata ends and content begins
                    uint currentWriteHead = metadataEnd;
                    outputStream.SetLength(currentWriteHead);

                    foreach (var block in contentBlocks)
                    {
                        Console.WriteLine($"  Processing Fileset {block.FilesetIndex + 1} [{block.BlockType}]:");

                        uint targetOffset;
                        if (block is ChunkBlock chunkBlock && chunkBlock.IsEnforced)
                        {
                            targetOffset = Align16(currentWriteHead);
                            Console.WriteLine($"    -> Enforced block. Appending at 0x{targetOffset:X8} (Size: {block.GetSize()})");
                        }
                        else
                        {
                            targetOffset = Align16(Math.Max(block.OriginalOffset, currentWriteHead));
                            Console.WriteLine($"    -> Placing at 0x{targetOffset:X8} (Size: {block.GetSize()})");
                        }

                        // Filling those gap between the current write head and the target offset with original data
                        if (targetOffset > currentWriteHead)
                        {
                            uint gapSize = targetOffset - currentWriteHead;
                            Console.WriteLine($"      -> Preserving {gapSize} bytes of unmanaged data from original file.");
                            // Ensuring that we won't read past the end of the original file
                            if (currentWriteHead + gapSize <= originalResData.Length)
                            {
                                writer.Write(originalResData, (int)currentWriteHead, (int)gapSize);
                            }
                            else
                            {
                                // This case is unlikely but safe to handle
                                writer.Write(new byte[gapSize]);
                            }
                        }

                        outputStream.Seek(targetOffset, SeekOrigin.Begin);

                        if (block is ChunkBlock cb)
                        {
                            writer.Write(cb.Data);
                            newChunkOffsets[cb.FilesetIndex] = targetOffset;
                        }
                        else if (block is NameBlock nameBlock)
                        {
                            var stringPointers = new List<uint>();
                            uint nameBlockBaseOffset = targetOffset;
                            uint currentStringDataOffset = nameBlockBaseOffset + (uint)nameBlock.Names.Length * 4;

                            foreach (var name in nameBlock.Names)
                            {
                                stringPointers.Add(currentStringDataOffset);
                                currentStringDataOffset += (uint)Encoding.Default.GetByteCount(name) + 1;
                            }

                            foreach (var pointer in stringPointers) writer.Write(pointer);
                            foreach (var name in nameBlock.Names)
                            {
                                writer.Write(Encoding.Default.GetBytes(name));
                                writer.Write((byte)0);
                            }
                            newNameOffsets[nameBlock.FilesetIndex] = nameBlockBaseOffset;
                        }

                        currentWriteHead = (uint)outputStream.Position;
                    }

                    // --- PASS 3: WRITE FINAL METADATA ---
                    Console.WriteLine("\n=== Pass 3: Writing Final Metadata ===");
                    outputStream.Seek(0, SeekOrigin.Begin);

                    writer.Write(_jsonData.MagicHeader);
                    writer.Write(_jsonData.GroupOffset);
                    writer.Write(_jsonData.GroupCount);
                    writer.Write(_jsonData.UNK1);
                    writer.Write(new byte[3]);
                    writer.Write(_jsonData.Configs);
                    writer.Write(new byte[12]);

                    outputStream.Seek(_jsonData.GroupOffset, SeekOrigin.Begin);
                    foreach (var jsonDataSet in _jsonData.DataSets)
                    {
                        writer.Write(jsonDataSet.Offset);
                        writer.Write(jsonDataSet.Count);
                    }

                    outputStream.Seek(0x60, SeekOrigin.Begin);
                    for (int i = 0; i < _resFile.Filesets.Count; i++)
                    {
                        var fileset = _resFile.Filesets[i];

                        if (newNameOffsets.TryGetValue(i, out uint newNameOffset))
                        {
                            fileset.OffsetName = newNameOffset;
                        }

                        if (newChunkOffsets.TryGetValue(i, out uint newChunkOffset))
                        {
                            fileset.RealOffset = newChunkOffset;
                            uint mask = fileset.AddressMode == "SET_C" ? SET_C_MASK : SET_D_MASK;
                            fileset.RawOffset = (newChunkOffset == 0) ? 0 : (mask | (newChunkOffset & 0x00FFFFFF));
                        }

                        if (fileset.Size == 0 && (fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D"))
                        {
                            fileset.RealOffset = 0;
                            fileset.RawOffset = 0;
                        }

                        writer.Write(fileset.RawOffset);
                        writer.Write(fileset.Size);
                        writer.Write(fileset.OffsetName);
                        writer.Write(fileset.ChunkName);
                        writer.Write(new byte[12]);
                        writer.Write(fileset.UnpackSize);
                    }
                    Console.WriteLine("Metadata and Fileset entries updated.");

                    // --- FINALIZE ---
                    string outputResFile = Path.Combine(
                        Path.GetDirectoryName(_inputResFile) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(_inputResFile) + (_enforcedInput ? "_enforced.res" : "_repacked.res")
                    );
                    File.WriteAllBytes(outputResFile, outputStream.ToArray());
                    Console.WriteLine($"\nRepacking complete. Output saved to {outputResFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking files: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        #region Load_And_Validate
        private void Load()
        {
            if (!File.Exists(_inputResFile))
                throw new FileNotFoundException($"Input .res file not found: {_inputResFile}");
            if (!File.Exists(_inputJsonFile))
                throw new FileNotFoundException($"Input .json file not found: {_inputJsonFile}");

            string jsonContent = File.ReadAllText(_inputJsonFile);
            _jsonData = JsonSerializer.Deserialize<JsonData>(jsonContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? throw new InvalidDataException("Failed to deserialize JSON file.");

            using (BinaryReader reader = new BinaryReader(File.Open(_inputResFile, FileMode.Open)))
            {
                _resFile = new RES_PSP(reader);
            }
            ValidateAndMap();
        }

        private void ValidateAndMap()
        {
            Console.WriteLine("=== Loading and Mapping RES File ===");
            if (_resFile.MagicHeader != _jsonData.MagicHeader) throw new InvalidDataException("MagicHeader mismatch");
            if (_resFile.GroupOffset != _jsonData.GroupOffset) throw new InvalidDataException("GroupOffset mismatch");
            if (_resFile.GroupCount != _jsonData.GroupCount) throw new InvalidDataException("GroupCount mismatch");
            if (_resFile.UNK1 != _jsonData.UNK1) throw new InvalidDataException("UNK1 mismatch");
            if (_resFile.Configs != _jsonData.Configs) throw new InvalidDataException("Configs mismatch");

            if (_resFile.DataSets.Count != _jsonData.DataSets.Count) throw new InvalidDataException("DataSets count mismatch");
            for (int i = 0; i < _resFile.DataSets.Count; i++)
            {
                if (_resFile.DataSets[i].Offset != _jsonData.DataSets[i].Offset || _resFile.DataSets[i].Count != _jsonData.DataSets[i].Count)
                    throw new InvalidDataException($"DataSet {i + 1} mismatch");
            }

            if (_resFile.Filesets.Count != _jsonData.Filesets.Count) throw new InvalidDataException("Filesets count mismatch");
            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var resFs = _resFile.Filesets[i];
                var jsonFs = _jsonData.Filesets[i];
                if (resFs.RawOffset != jsonFs.RawOffset) throw new InvalidDataException($"Fileset {i + 1} RawOffset mismatch");
                if (resFs.Size != jsonFs.Size) throw new InvalidDataException($"Fileset {i + 1} Size mismatch");
                if (resFs.OffsetName != jsonFs.OffsetName) throw new InvalidDataException($"Fileset {i + 1} OffsetName mismatch");
                if (resFs.ChunkName != jsonFs.ChunkName) throw new InvalidDataException($"Fileset {i + 1} ChunkName mismatch");
                if (resFs.UnpackSize != jsonFs.UnpackSize) throw new InvalidDataException($"Fileset {i + 1} UnpackSize mismatch");
                if (resFs.AddressMode != jsonFs.AddressMode) throw new InvalidDataException($"Fileset {i + 1} AddressMode mismatch");
            }
            Console.WriteLine("Load and Mapping Complete.");
        }
        #endregion

        #region RDP_Helpers
        private uint DeterminePreferredMask()
        {
            int setCCount = _jsonData.Filesets.Count(f => f.AddressMode == "SET_C");
            int setDCount = _jsonData.Filesets.Count(f => f.AddressMode == "SET_D");
            return setCCount >= setDCount ? SET_C_MASK : SET_D_MASK;
        }

        private Dictionary<string, List<RDPDictionaryEntry>> LoadRDPDictionaries()
        {
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
        #endregion
    }
}