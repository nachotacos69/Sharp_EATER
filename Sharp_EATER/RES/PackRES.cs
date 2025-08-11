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
                    // Pointer is out of bounds, can't determine size. Assume it's just the pointer table.
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

        public void Repack()
        {
            try
            {
                Load();
                uint preferredMask = DeterminePreferredMask();
                byte[] originalResData = File.ReadAllBytes(_inputResFile);

                // --- PASS 1: STAGE MODIFIED/PRESERVED CONTENT BLOCKS ---
                Console.WriteLine("=== Pass 1: Staging Content Blocks ===");
                var stagedBlocks = new Dictionary<uint, ManagedBlock>();
                var originalBlockMap = new Dictionary<uint, uint>(); // Map of original start offset to original length

                for (int i = 0; i < _resFile.Filesets.Count; i++)
                {
                    var fileset = _resFile.Filesets[i];
                    var jsonFileset = _jsonData.Filesets[i];
                    string originalAddressMode = fileset.AddressMode;

                    bool isEnforced = _enforcedInput && (originalAddressMode == "Package" || originalAddressMode == "Data" || originalAddressMode == "Patch");
                    if (isEnforced) fileset.AddressMode = preferredMask == SET_C_MASK ? "SET_C" : "SET_D";
                    bool isSetCSD = fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D";

                    // Map and stage file data chunks
                    if (isSetCSD && (fileset.Size > 0 || !string.IsNullOrEmpty(jsonFileset.Filename)))
                    {
                        if (fileset.RealOffset > 0 && !originalBlockMap.ContainsKey(fileset.RealOffset))
                        {
                            originalBlockMap[fileset.RealOffset] = fileset.Size;
                        }

                        byte[] chunkData;
                        bool isReplaced = !string.IsNullOrEmpty(jsonFileset.Filename) && File.Exists(jsonFileset.Filename);
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
                                if (_resFile.Filesets[i].UnpackSize == 0) fileset.UnpackSize = 0; else fileset.UnpackSize = fileset.Size;
                                // Some unpackSize are meant to be zero by default with no value despite being raw data
                                // and size being present within its Fileset Structure..
                                // so this is kind of a basic fix on this. to avoid some zero default unpacksize having value.
                            }
                        }
                        else
                        {
                            chunkData = new byte[fileset.Size];
                            Array.Copy(originalResData, (int)fileset.RealOffset, chunkData, 0, (int)fileset.Size);
                        }
                        uint offset = isEnforced ? 0 : fileset.RealOffset;
                        stagedBlocks[offset] = new ManagedBlock(chunkData, i, "Chunk");
                    }

                    // Map and stage name blocks
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
                    if (startOffset < fileCursor) continue; // Skip overlapping blocks if any

                    // Add unmanaged data that exists between the previous block and this one, but only if not all zeros
                    if (startOffset > fileCursor)
                    {
                        byte[] gapData = originalResData.Skip((int)fileCursor).Take((int)(startOffset - fileCursor)).ToArray();
                        if (!gapData.All(b => b == 0))
                        {
                            finalLayout.Add(new UnmanagedBlock(gapData));
                        }
                        // else skip; minimal padding will be added dynamically via alignment if needed for the next managed block
                    }

                    // Add the managed block itself (which is always in stagedBlocks).
                    if (stagedBlocks.ContainsKey(startOffset))
                    {
                        finalLayout.Add(stagedBlocks[startOffset]);
                    }

                    // Advance the cursor past the space occupied by the *original* block.
                    fileCursor = startOffset + originalBlockMap[startOffset];
                }

                // Add any remaining unmanaged data from the end of the last known block to the end of the file, but only if not all zeros
                if (fileCursor < originalResData.Length)
                {
                    byte[] trailingData = originalResData.Skip((int)fileCursor).ToArray();
                    if (!trailingData.All(b => b == 0))
                    {
                        finalLayout.Add(new UnmanagedBlock(trailingData));
                    }
                    // else skip; no excessive trailing padding will be added
                }

                // Add enforced blocks at the very end of the file layout.
                foreach (var enforcedBlock in stagedBlocks.Where(b => b.Key == 0))
                {
                    finalLayout.Add(enforcedBlock.Value);
                }

                // --- PASS 2: WRITE FINAL LAYOUT AND CALCULATE NEW OFFSETS ---
                Console.WriteLine("\n=== Pass 2: Writing Final Layout ===");
                var newChunkOffsets = new Dictionary<int, uint>();
                var newNameOffsets = new Dictionary<int, uint>();
                uint newConfigsValue = metadataEnd; // Initialize with the end of the fileset table

                using (var outputStream = new MemoryStream())
                using (var writer = new BinaryWriter(outputStream))
                {
                    writer.Write(originalResData, 0, (int)metadataEnd);
                    uint currentWriteHead = metadataEnd;

                    foreach (var block in finalLayout)
                    {
                        // Align managed blocks to ensure correct padding
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

                    // Ensure final padding to the next 16-byte boundary
                    uint finalSize = (uint)outputStream.Length;
                    uint paddingNeeded = Align16(finalSize) - finalSize;
                    if (paddingNeeded > 0 && paddingNeeded <= 15)
                    {
                        byte[] padding = new byte[paddingNeeded];
                        writer.Write(padding);
                        currentWriteHead = (uint)outputStream.Position;
                       // Console.WriteLine($"Added {paddingNeeded} bytes of padding at 0x{finalSize:X8} to align file to 16-byte boundary.");
                    }

                    // The Configs value is the aligned offset of the end of the last name block.
                    newConfigsValue = Align16(newConfigsValue);
                    Console.WriteLine($"\nNew calculated Configs value: 0x{newConfigsValue:X8}");

                    // --- PASS 3: WRITE FINAL METADATA ---
                    Console.WriteLine("\n=== Pass 3: Writing Final Metadata ===");
                    outputStream.Seek(0, SeekOrigin.Begin);
                    // Use the newly calculated Configs value
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
                            // Fix name pointers
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
                        if (fileset.Size == 0 && (fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D")) { fileset.RealOffset = 0; fileset.RawOffset = 0; }

                        writer.Write(fileset.RawOffset); writer.Write(fileset.Size); writer.Write(fileset.OffsetName); writer.Write(fileset.ChunkName); writer.Write(new byte[12]); writer.Write(fileset.UnpackSize);
                    }
                    Console.WriteLine("Metadata and Fileset entries updated.");

                    // --- FINALIZE ---
                    string outputResFile = Path.Combine(Path.GetDirectoryName(_inputResFile) ?? string.Empty, Path.GetFileNameWithoutExtension(_inputResFile) + (_enforcedInput ? "_enforced.res" : "_repacked.res"));
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
            if (!File.Exists(_inputResFile)) throw new FileNotFoundException($"Input .res file not found: {_inputResFile}");
            if (!File.Exists(_inputJsonFile)) throw new FileNotFoundException($"Input .json file not found: {_inputJsonFile}");
            string jsonContent = File.ReadAllText(_inputJsonFile);
            _jsonData = JsonSerializer.Deserialize<JsonData>(jsonContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? throw new InvalidDataException("Failed to deserialize JSON file.");
            using (BinaryReader reader = new BinaryReader(File.Open(_inputResFile, FileMode.Open))) { _resFile = new RES_PSP(reader); }
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
                string jsonPath = rdpFiles[i]; string rdpName = rdpNames[i];
                if (!File.Exists(jsonPath)) { Console.WriteLine($"Dictionary {jsonPath} not found, skipping {rdpName}."); continue; }
                try
                {
                    string jsonContent = File.ReadAllText(jsonPath);
                    var entries = JsonSerializer.Deserialize<List<RDPDictionaryEntry>>(jsonContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    if (entries == null || entries.Count == 0) { Console.WriteLine($"Dictionary {jsonPath} is empty, skipping {rdpName}."); continue; }
                    dictionaries[rdpName] = entries.OrderBy(e => e.Index).ToList();
                    Console.WriteLine($"Loaded dictionary {jsonPath} with {entries.Count} entries for {rdpName}.");
                }
                catch (Exception ex) { Console.WriteLine($"Failed to load dictionary {jsonPath}: {ex.Message}, skipping {rdpName}."); }
            }
            return dictionaries;
        }

        private Dictionary<string, (FileStream Stream, BinaryWriter Writer, string OutputPath)> PrepareRDPStreams()
        {
            var streams = new Dictionary<string, (FileStream, BinaryWriter, string)>();
            string[] rdpFiles = { "package.rdp", "data.rdp", "patch.rdp" };
            foreach (var rdpFile in rdpFiles)
            {
                if (!File.Exists(rdpFile)) { Console.WriteLine($"RDP file {rdpFile} not found, skipping."); continue; }
                string outputRdpFile = Path.Combine(Path.GetDirectoryName(rdpFile) ?? string.Empty, Path.GetFileNameWithoutExtension(rdpFile) + "_new.rdp");
                try
                {
                    File.Copy(rdpFile, outputRdpFile, true);
                    var stream = new FileStream(outputRdpFile, FileMode.Open, FileAccess.ReadWrite);
                    var writer = new BinaryWriter(stream);
                    streams[rdpFile] = (stream, writer, outputRdpFile);
                    Console.WriteLine($"Prepared RDP stream for {outputRdpFile}");
                }
                catch (Exception ex) { Console.WriteLine($"Failed to prepare RDP stream for {outputRdpFile}: {ex.Message}, skipping."); }
            }
            return streams;
        }
        #endregion
    }
}