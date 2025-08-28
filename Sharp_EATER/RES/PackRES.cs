﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SharpRES
{
    
    public class EnforcementRule
    {
        public List<string> SourceModes { get; set; } = new List<string>();
        public string TargetMode { get; set; }
        public bool IsRdpToRes { get; set; }
        public bool IsRdpToRdp { get; set; }
    }

    public class PackRES
    {
        private readonly string _inputResFile;
        private readonly string _inputJsonFile;
        private readonly EnforcementRule _enforcementRule;
        private readonly Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> _rdpStreams;
        private readonly Dictionary<string, long> _rdpCursors;

        private RES_PSP _resFile;
        private JsonData _jsonData;
        private const uint SET_C_MASK = 0xC0000000;
        private const uint SET_D_MASK = 0xD0000000;
        private const long RDP_ALIGNMENT = 0x800;

        #region Nested Classes (Data Structures)

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

        // --- Content Block Abstractions for Repacking ---
        private abstract class ContentBlock
        {
            public abstract byte[] GetData(PackRES context);
        }

        private class ManagedBlock : ContentBlock
        {
            public int FilesetIndex { get; }
            public string BlockType { get; }
            private byte[] _data;

            public ManagedBlock(byte[] data, int filesetIndex, string blockType)
            {
                _data = data;
                FilesetIndex = filesetIndex;
                BlockType = blockType;
            }

            public override byte[] GetData(PackRES context)
            {
                if (BlockType == "Names" && _data == null)
                {
                    var names = context._jsonData.Filesets[FilesetIndex].Names;
                    using (var ms = new MemoryStream())
                    using (var writer = new BinaryWriter(ms))
                    {
                        // Writing placeholder pointers; they will be fixed in the final pass
                        for (int i = 0; i < names.Length; i++) writer.Write((uint)0);
                        foreach (var name in names)
                        {
                            writer.Write(Encoding.Default.GetBytes(name));
                            writer.Write((byte)0);
                        }
                        _data = ms.ToArray();
                    }
                }
                return _data;
            }
        }

        private class UnmanagedBlock : ContentBlock
        {
            private readonly byte[] _data;
            public UnmanagedBlock(byte[] data) { _data = data; }
            public override byte[] GetData(PackRES context) => _data;
        }

        #endregion

        #region Constructor

        public PackRES(string inputResFile, string inputJsonFile, EnforcementRule rule,
                       Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> rdpStreams,
                       Dictionary<string, long> rdpCursors)
        {
            _inputResFile = inputResFile ?? throw new ArgumentNullException(nameof(inputResFile));
            _inputJsonFile = inputJsonFile ?? throw new ArgumentNullException(nameof(inputJsonFile));
            _enforcementRule = rule;
            _rdpStreams = rdpStreams;
            _rdpCursors = rdpCursors;
        }

        #endregion

        #region Public Methods

        public void Repack()
        {
            try
            {
                byte[] originalResData = PrepareOriginalData();
                Load(originalResData);
                uint preferredMask = DeterminePreferredMask();

                // --- PASS 1: STAGE MODIFIED/PRESERVED CONTENT BLOCKS ---
                Console.WriteLine("=== Pass 1: Staging Content Blocks ===");
                var stagedBlocks = new Dictionary<uint, ManagedBlock>();
                var originalBlockMap = new Dictionary<uint, uint>(); // Map of original start offset to original length

                for (int i = 0; i < _resFile.Filesets.Count; i++)
                {
                    var fileset = _resFile.Filesets[i];
                    var jsonFileset = _jsonData.Filesets[i];
                    string originalAddressMode = fileset.AddressMode;
                    bool isEnforced = false;

                    // Check for and apply enforcement rules
                    if (_enforcementRule != null && _enforcementRule.SourceModes.Contains(originalAddressMode))
                    {
                        isEnforced = true;
                        if (_enforcementRule.IsRdpToRes)
                        {
                            fileset.AddressMode = _enforcementRule.TargetMode == "SET_C" ? "SET_C" : "SET_D";
                            Console.WriteLine($"  Fileset {i + 1}: Enforcing '{originalAddressMode}' to '{fileset.AddressMode}'. Data will be moved to RES file.");
                        }
                        else if (_enforcementRule.IsRdpToRdp)
                        {
                            HandleRdpToRdpEnforcement(i, fileset, jsonFileset, originalAddressMode);
                            // Data is written to RDP, not staged for RES. Continue to process name block.
                        }
                    }

                    bool isSetCSD = fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D";

                    // Map and stage file data chunks for local RES storage (SET_C/SET_D).
                    if (isSetCSD)
                    {
                        bool isReplaced = !string.IsNullOrEmpty(jsonFileset.Filename) && File.Exists(jsonFileset.Filename);
                        bool hasOriginalBlock = !isEnforced && fileset.RealOffset > 0;

                        if (isReplaced || hasOriginalBlock)
                        {
                            if (hasOriginalBlock && !originalBlockMap.ContainsKey(fileset.RealOffset))
                            {
                                originalBlockMap[fileset.RealOffset] = fileset.Size;
                            }

                            byte[] chunkData;
                            if (isReplaced)
                            {
                                byte[] rawData = File.ReadAllBytes(jsonFileset.Filename);
                                bool isCompressed = jsonFileset.CompressedBLZ2 == true || jsonFileset.CompressedBLZ4 == true;
                                if (isCompressed)
                                {
                                    fileset.UnpackSize = (uint)rawData.Length;
                                    if (jsonFileset.CompressedBLZ2 == true) chunkData = Compression.LeCompression(rawData); else chunkData = BLZ4Utils.PackBLZ4Data(rawData);
                                    fileset.Size = (uint)chunkData.Length;
                                }
                                else
                                {
                                    chunkData = rawData;
                                    fileset.Size = (uint)rawData.Length;
                                    fileset.UnpackSize = (_resFile.Filesets[i].UnpackSize == 0) ? 0 : fileset.Size;
                                }
                            }
                            else // Preserving original data
                            {
                                chunkData = new byte[fileset.Size];
                                if (fileset.Size > 0)
                                {
                                    Array.Copy(originalResData, (int)fileset.RealOffset, chunkData, 0, (int)fileset.Size);
                                }
                            }
                            uint offset = isEnforced ? 0 : fileset.RealOffset;
                            stagedBlocks[offset] = new ManagedBlock(chunkData, i, "Chunk");
                        }
                    }

                    // Map and stage name blocks (for all fileset types)
                    if (fileset.OffsetName != 0 && jsonFileset.Names != null && jsonFileset.Names.Length > 0)
                    {
                        if (!originalBlockMap.ContainsKey(fileset.OffsetName))
                        {
                            originalBlockMap[fileset.OffsetName] = GetOriginalNameBlockSize(i, originalResData);
                        }
                        stagedBlocks[fileset.OffsetName] = new ManagedBlock(null, i, "Names");
                    }
                }
                Console.WriteLine($"Staged {stagedBlocks.Count(b => b.Key > 0)} managed content blocks for repacking.");

                // --- PASS HALFWAY: BUILD THE FINAL, COMPLETE LAYOUT ---
                var finalLayout = new List<ContentBlock>();
                uint metadataEnd = (uint)(0x60 + _resFile.Filesets.Count * 32);
                uint fileCursor = metadataEnd;

                var sortedOffsets = originalBlockMap.Keys.ToList();
                sortedOffsets.Sort();

                foreach (var startOffset in sortedOffsets)
                {
                    if (startOffset < fileCursor) continue;

                    if (startOffset > fileCursor)
                    {
                        byte[] gapData = originalResData.Skip((int)fileCursor).Take((int)(startOffset - fileCursor)).ToArray();
                        if (!gapData.All(b => b == 0))
                        {
                            finalLayout.Add(new UnmanagedBlock(gapData));
                        }
                    }

                    if (stagedBlocks.ContainsKey(startOffset))
                    {
                        finalLayout.Add(stagedBlocks[startOffset]);
                    }

                    fileCursor = startOffset + originalBlockMap[startOffset];
                }

                if (fileCursor < originalResData.Length)
                {
                    byte[] trailingData = originalResData.Skip((int)fileCursor).ToArray();
                    if (!trailingData.All(b => b == 0))
                    {
                        finalLayout.Add(new UnmanagedBlock(trailingData));
                    }
                }

                foreach (var enforcedBlock in stagedBlocks.Where(b => b.Key == 0))
                {
                    finalLayout.Add(enforcedBlock.Value);
                }

                // --- PASS 2: WRITE FINAL LAYOUT AND CALCULATE NEW OFFSETS ---
                Console.WriteLine("\n=== Pass 2: Writing Final Layout ===");
                var newChunkOffsets = new Dictionary<int, uint>();
                var newNameOffsets = new Dictionary<int, uint>();
                uint newConfigsValue = metadataEnd;

                using (var outputStream = new MemoryStream())
                using (var writer = new BinaryWriter(outputStream))
                {
                    writer.Write(originalResData, 0, (int)metadataEnd);
                    uint currentWriteHead = metadataEnd;

                    foreach (var block in finalLayout)
                    {
                        if (block is ManagedBlock)
                        {
                            currentWriteHead = Align16(currentWriteHead);
                        }

                        outputStream.Seek(currentWriteHead, SeekOrigin.Begin);
                        byte[] data = block.GetData(this);

                        if (block is ManagedBlock managed)
                        {
                            if (managed.BlockType == "Chunk")
                            {
                                newChunkOffsets[managed.FilesetIndex] = currentWriteHead;
                                Console.WriteLine($"  Fileset {managed.FilesetIndex + 1} [Chunk]: Wrote {data.Length} bytes at 0x{currentWriteHead:X8}");
                            }
                            else if (managed.BlockType == "Names")
                            {
                                newNameOffsets[managed.FilesetIndex] = currentWriteHead;
                                Console.WriteLine($"  Fileset {managed.FilesetIndex + 1} [Names]: Wrote {data.Length} bytes at 0x{currentWriteHead:X8}");
                                newConfigsValue = Math.Max(newConfigsValue, currentWriteHead + (uint)data.Length);
                            }
                        }
                        writer.Write(data);
                        currentWriteHead = (uint)outputStream.Position;
                    }

                    uint finalSize = (uint)outputStream.Length;
                    uint paddingNeeded = Align16(finalSize) - finalSize;
                    if (paddingNeeded > 0 && paddingNeeded <= 15)
                    {
                        writer.Write(new byte[paddingNeeded]);
                    }

                    newConfigsValue = Align16(newConfigsValue);
                    Console.WriteLine($"\nNew calculated Configs value: 0x{newConfigsValue:X8}");

                    // --- PASS 3: WRITE FINAL METADATA ---
                    Console.WriteLine("\n=== Pass 3: Writing Final Metadata ===");
                    outputStream.Seek(0, SeekOrigin.Begin);
                    writer.Write(_jsonData.MagicHeader); writer.Write(_jsonData.GroupOffset); writer.Write(_jsonData.GroupCount); writer.Write(_jsonData.UNK1); writer.Write(new byte[3]); writer.Write(newConfigsValue); writer.Write(new byte[12]);
                    outputStream.Seek(_jsonData.GroupOffset, SeekOrigin.Begin);
                    foreach (var ds in _jsonData.DataSets) { writer.Write(ds.Offset); writer.Write(ds.Count); }

                    outputStream.Seek(0x60, SeekOrigin.Begin);
                    for (int i = 0; i < _resFile.Filesets.Count; i++)
                    {
                        var fileset = _resFile.Filesets[i];
                        if (newNameOffsets.TryGetValue(i, out uint newNameOffset))
                        {
                            fileset.OffsetName = newNameOffset;
                            var names = _jsonData.Filesets[i].Names;
                            uint currentStringDataOffset = newNameOffset + (uint)names.Length * 4;
                            long originalPos = outputStream.Position;
                            outputStream.Seek(newNameOffset, SeekOrigin.Begin);
                            foreach (var name in names)
                            {
                                writer.Write(currentStringDataOffset);
                                currentStringDataOffset += (uint)Encoding.Default.GetByteCount(name) + 1;
                            }
                            outputStream.Seek(originalPos, SeekOrigin.Begin);
                        }
                        if (newChunkOffsets.TryGetValue(i, out uint newChunkOffset))
                        {
                            fileset.RealOffset = newChunkOffset;
                            uint mask = fileset.AddressMode == "SET_C" ? SET_C_MASK : SET_D_MASK;
                            fileset.RawOffset = (newChunkOffset == 0) ? 0 : (mask | (newChunkOffset & 0x00FFFFFF));
                        }

                        writer.Write(fileset.RawOffset); writer.Write(fileset.Size); writer.Write(fileset.OffsetName); writer.Write(fileset.ChunkName); writer.Write(new byte[12]); writer.Write(fileset.UnpackSize);
                    }
                    Console.WriteLine("Metadata and Fileset entries updated.");

                    // --- FINALIZE ---
                    File.WriteAllBytes(_inputResFile, outputStream.ToArray());
                    Console.WriteLine($"\nRepacking complete. Output saved to {_inputResFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error repacking files: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        #endregion

        #region Enforcement Logic

        private void HandleRdpToRdpEnforcement(int filesetIndex, RES_PSP.Fileset fileset, JsonFileset jsonFileset, string originalAddressMode)
        {
            string targetRdpName = Program.GetRdpFileNameFromMode(_enforcementRule.TargetMode);
            if (targetRdpName == null || !_rdpStreams.ContainsKey(targetRdpName))
            {
                // This should be caught by Program.cs, but as a safeguard:
                Console.WriteLine($"  Fileset {filesetIndex + 1}: [Warning] Cannot enforce to '{_enforcementRule.TargetMode}' because target RDP stream is not available. Skipping enforcement.");
                return;
            }

            if (string.IsNullOrEmpty(jsonFileset.Filename) || !File.Exists(jsonFileset.Filename))
            {
                Console.WriteLine($"  Fileset {filesetIndex + 1}: [Warning] Cannot enforce '{originalAddressMode}' because replacement file '{jsonFileset.Filename ?? "null"}' does not exist. Preserving original entry.");
                return;
            }

            var (stream, writer, _) = _rdpStreams[targetRdpName];
            long currentCursor = _rdpCursors[targetRdpName];

            // 1. Calculate next aligned offset and required padding
            long nextAlignedOffset = (currentCursor + (RDP_ALIGNMENT - 1)) & ~(RDP_ALIGNMENT - 1);
            long paddingNeeded = nextAlignedOffset - currentCursor;

            // 2. Read and compress file data
            byte[] rawData = File.ReadAllBytes(jsonFileset.Filename);
            byte[] chunkData;
            bool isCompressed = jsonFileset.CompressedBLZ2 == true || jsonFileset.CompressedBLZ4 == true;
            if (isCompressed)
            {
                fileset.UnpackSize = (uint)rawData.Length;
                if (jsonFileset.CompressedBLZ2 == true) chunkData = Compression.LeCompression(rawData); else chunkData = BLZ4Utils.PackBLZ4Data(rawData);
                fileset.Size = (uint)chunkData.Length;
            }
            else
            {
                chunkData = rawData;
                fileset.Size = (uint)rawData.Length;
                fileset.UnpackSize = (fileset.UnpackSize == 0) ? 0 : fileset.Size;
            }

            // 3. Write padding and data to RDP file
            writer.BaseStream.Seek(currentCursor, SeekOrigin.Begin);
            if (paddingNeeded > 0)
            {
                writer.Write(new byte[paddingNeeded]);
            }
            writer.Write(chunkData);

            // 4. Update the RDP cursor for the next file
            _rdpCursors[targetRdpName] = nextAlignedOffset + chunkData.Length;

            // 5. Update the in-memory fileset with new RDP location info
            fileset.AddressMode = _enforcementRule.TargetMode;
            fileset.RealOffset = (uint)nextAlignedOffset;
            fileset.RawOffset = Program.GetRawOffsetFromRealOffset(fileset.RealOffset, fileset.AddressMode);

            Console.WriteLine($"  Fileset {filesetIndex + 1}: Enforced '{originalAddressMode}' to '{fileset.AddressMode}'. Wrote {chunkData.Length} bytes to {targetRdpName} at 0x{fileset.RealOffset:X8} (New RawOffset: 0x{fileset.RawOffset:X8})");
        }

        #endregion

        #region File Loading and Validation

        private byte[] PrepareOriginalData()
        {
            string backupFile = _inputResFile + ".bak";

            if (File.Exists(backupFile))
            {
                Console.WriteLine($"Backup found: {backupFile}. Using it as the original source for repacking.");
                return File.ReadAllBytes(backupFile);
            }
            else
            {
                if (!File.Exists(_inputResFile))
                {
                    throw new FileNotFoundException($"Input .res file not found and no backup exists: {_inputResFile}");
                }
                Console.WriteLine($"No backup found. Creating new backup: {backupFile}");
                byte[] originalData = File.ReadAllBytes(_inputResFile);
                File.WriteAllBytes(backupFile, originalData);
                return originalData;
            }
        }

        private void Load(byte[] originalResData)
        {
            if (!File.Exists(_inputJsonFile)) throw new FileNotFoundException($"Input .json file not found: {_inputJsonFile}");
            string jsonContent = File.ReadAllText(_inputJsonFile);
            _jsonData = JsonSerializer.Deserialize<JsonData>(jsonContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? throw new InvalidDataException("Failed to deserialize JSON file.");

            using (MemoryStream ms = new MemoryStream(originalResData))
            using (BinaryReader reader = new BinaryReader(ms))
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
            for (int i = 0; i < _resFile.DataSets.Count; i++) { if (_resFile.DataSets[i].Offset != _jsonData.DataSets[i].Offset || _resFile.DataSets[i].Count != _jsonData.DataSets[i].Count) throw new InvalidDataException($"DataSet {i + 1} mismatch"); }
            if (_resFile.Filesets.Count != _jsonData.Filesets.Count) throw new InvalidDataException("Filesets count mismatch");
            for (int i = 0; i < _resFile.Filesets.Count; i++)
            {
                var resFs = _resFile.Filesets[i]; var jsonFs = _jsonData.Filesets[i];
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

        #region Utility Methods

        private uint Align16(uint offset)
        {
            return (offset + 15) & ~15u;
        }

        private uint GetOriginalNameBlockSize(int filesetIndex, byte[] originalResData)
        {
            var fileset = _resFile.Filesets[filesetIndex];
            if (fileset.OffsetName == 0 || fileset.ChunkName == 0)
            {
                return 0;
            }

            using (var ms = new MemoryStream(originalResData))
            using (var reader = new BinaryReader(ms))
            {
                reader.BaseStream.Seek(fileset.OffsetName, SeekOrigin.Begin);

                uint[] pointers = new uint[fileset.ChunkName];
                uint maxPointer = 0;
                for (int i = 0; i < fileset.ChunkName; i++)
                {
                    pointers[i] = reader.ReadUInt32();
                    if (pointers[i] > maxPointer)
                    {
                        maxPointer = pointers[i];
                    }
                }

                if (maxPointer == 0)
                {
                    return fileset.ChunkName * 4;
                }

                if (maxPointer >= originalResData.Length)
                {
                    return fileset.ChunkName * 4;
                }

                long endOfStringOffset = maxPointer;
                while (endOfStringOffset < originalResData.Length && originalResData[endOfStringOffset] != 0)
                {
                    endOfStringOffset++;
                }
                endOfStringOffset++; // Include the null terminator

                return (uint)(endOfStringOffset - fileset.OffsetName);
            }
        }

        private uint DeterminePreferredMask()
        {
            int setCCount = _jsonData.Filesets.Count(f => f.AddressMode == "SET_C");
            int setDCount = _jsonData.Filesets.Count(f => f.AddressMode == "SET_D");
            return setCCount >= setDCount ? SET_C_MASK : SET_D_MASK;
        }

        #endregion
    }
}