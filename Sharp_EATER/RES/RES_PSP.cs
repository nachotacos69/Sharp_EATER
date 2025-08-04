using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace SharpRES
{
    public class RES_PSP
    {
        // Header structure (32 bytes)
        public uint MagicHeader { get; private set; } // 4 bytes, expected 0x73657250
        public uint GroupOffset { get; private set; } // 4 bytes, offset to DataSet
        public byte GroupCount { get; private set; } // 1 byte, number of DataSet groups
        public uint UNK1 { get; private set; } // 4 bytes, undocumented
        // 3 bytes padding (skipped)
        public uint Configs { get; private set; } // 4 bytes, length of overall configuration (from header to the name structure before hitting some fileset chunks)
        // 12 bytes padding (skipped)
        // Total: 32 bytes. (4 + 4 + 1 + 4 + 3 + 4 + 12 = 32)

        // DataSet structure (8 bytes per group)
        public class DataSet
        {
            public uint Offset { get; set; } // 4 bytes, pointer to Fileset Data
            public uint Count { get; set; } // 4 bytes, number of Fileset entries
        }
        public List<DataSet> DataSets { get; private set; }

        
        public class Fileset
        {
            // Fileset Structure (32 bytes per entry)
            public uint RawOffset { get; set; } // 4 bytes, raw offset with address mode
            public uint Size { get; set; } // 4 bytes, chunk size
            public uint OffsetName { get; set; } // 4 bytes, offset to name data
            public uint ChunkName { get; set; } // 4 bytes, chunk name index

            public uint UnpackSize { get; set; } // 4 bytes, true size of chunk



            public string[] Names { get; set; } // Parsed names (name, type, directories)
            public uint RealOffset { get; set; } // Processed offset after masking
            public string AddressMode { get; set; } // Type of offset (e.g., Current, RDP)
            public uint[] NamesPointer { get; set; } // Pointers to name data
            public bool? CompressedBLZ2 { get; set; } // True if BLZ2 compressed, false if not, null if not extracted
            public bool? CompressedBLZ4 { get; set; } // True if BLZ4 compressed, false if not, null if not extracted
            public string Filename { get; set; } // Path to extracted file

            // Returns an array indicating presence (true) or absence (false) of fields
            public bool[] GetFilesetPointers()
            {
                return new[]
                {
                    RawOffset != 0,
                    Size != 0,
                    OffsetName != 0,
                    ChunkName != 0,
                    UnpackSize != 0
                    /* I uses bools to properly track things.
                     * This is useful when it comes to data that are zero or invalid.
                     * Or better yet, data being empty or not part of the valid Fileset provided.
                     */
                };
            }
        }
        public List<Fileset> Filesets { get; private set; }

        public RES_PSP(BinaryReader reader)
        {
            DataSets = new List<DataSet>();
            Filesets = new List<Fileset>();

            // Read Header (32 bytes)
            MagicHeader = reader.ReadUInt32();
            if (MagicHeader != 0x73657250)
                throw new InvalidDataException("Invalid .res file: Magic header mismatch.");

            GroupOffset = reader.ReadUInt32();
            GroupCount = reader.ReadByte();
            UNK1 = reader.ReadUInt32();
            reader.BaseStream.Seek(3, SeekOrigin.Current); // Skip 3 bytes padding
            Configs = reader.ReadUInt32();
            reader.BaseStream.Seek(12, SeekOrigin.Current); // Skip 12 bytes padding

            // Read DataSets (64 bytes total, 8 bytes per group)
            reader.BaseStream.Seek(GroupOffset, SeekOrigin.Begin);
            for (int i = 0; i < 8; i++) // Fixed 8 groups
            {
                DataSets.Add(new DataSet
                {
                    Offset = reader.ReadUInt32(),
                    Count = reader.ReadUInt32()
                });
            }

            // Read Fileset Data starting at 0x60
            reader.BaseStream.Seek(0x60, SeekOrigin.Begin);
            uint totalFilesetCount = 0;
            foreach (var dataset in DataSets)
                totalFilesetCount += dataset.Count;

            for (uint i = 0; i < totalFilesetCount; i++)
            {
                Fileset fileset = new Fileset
                {
                    RawOffset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    OffsetName = reader.ReadUInt32(),
                    ChunkName = reader.ReadUInt32()
                };
                reader.BaseStream.Seek(12, SeekOrigin.Current);
                // 12 bytes padding (skipped) 

                fileset.UnpackSize = reader.ReadUInt32();

                // Process offset and address mode
                fileset.AddressMode = GetAddressMode(fileset.RawOffset);
                fileset.RealOffset = ProcessOffset(fileset.RawOffset, fileset.AddressMode);

                // Read names and pointers if OffsetName is valid
                if (fileset.OffsetName != 0)
                {
                    (string[] names, uint[] pointers) = ReadNames(reader, fileset.OffsetName, fileset.ChunkName);
                    fileset.Names = names;
                    fileset.NamesPointer = pointers;
                }

                Filesets.Add(fileset);
            }
        }

        private string GetAddressMode(uint rawOffset)
        {
            byte mode = (byte)(rawOffset >> 24);
            switch (mode)
            {
                case 0x00: return "Reserve"; 
                case 0x30: return "DataSet"; 
                case 0x40: return "Package"; 
                case 0x50: return "Data"; 
                case 0x60: return "Patch";
                case 0xC0: return "SET_C";
                case 0xD0: return "SET_D";
                default: return "Invalid";
                    /* "Reserve" -> Used to define some files as invalid/empty
                     * "DataSet" -> Defines some files that are within `data_` prefixes folders (this is a replacement for data.rdp)
                     * "Package" -> Package File contains heavy data of the game (audio, cutscene and mission datas, character and enemy textures, and other things)
                     * "Data" -> Data File contains medium data of the game (usually just textures for other characters/players, some audios, some enemy data, and other things)
                     * "Patch" -> Patch File contains extra contents of the game (DLC)
                     * "SET_C" or "SET_D" -> These two are usually sets of data that are local and can be found within a RES file (different per RES file)
                     */
            }
        }

        private uint ProcessOffset(uint rawOffset, string addressMode)
        {
            uint offset = rawOffset & 0x00FFFFFF; // Mask first byte
            if (addressMode == "Package" || addressMode == "Data" || addressMode == "Patch")
                offset *= 0x800; // Multiply for RDP files
            return offset;
        }

        private (string[], uint[]) ReadNames(BinaryReader reader, uint offsetName, uint chunkName)
        {
            long originalPosition = reader.BaseStream.Position;
            reader.BaseStream.Seek(offsetName, SeekOrigin.Begin);

            // Read pointers (chunkName * 4 bytes)
            uint[] pointers = new uint[chunkName];
            for (uint i = 0; i < chunkName; i++)
                pointers[i] = reader.ReadUInt32();

            List<string> names = new List<string>();
            foreach (uint pointer in pointers)
            {
                reader.BaseStream.Seek(pointer, SeekOrigin.Begin);
                StringBuilder sb = new StringBuilder();
                char c;
                while ((c = reader.ReadChar()) != '\0')
                    sb.Append(c);
                // Always add the string, even if empty
                names.Add(sb.ToString());
            }

            reader.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            return (names.ToArray(), pointers);
        }

        public string Serialize() //JSON Serialization here
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var serializedData = new
            {
                MagicHeader,
                GroupOffset,
                GroupCount,
                UNK1,
                Configs,
                DataSets = DataSets.Select(ds => new
                {
                    ds.Offset,
                    ds.Count
                }).ToList(),
                Filesets = Filesets.Select(fs => new
                {
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

            return JsonSerializer.Serialize(serializedData, options);
        }
    }
}