using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RESExtractor
{
    public class RES_PSP
    {
        // Header structure (32 bytes)
        public uint MagicHeader { get; private set; } // 4 bytes, expected 0x73657250
        public uint GroupOffset { get; private set; } // 4 bytes, offset to DataSet
        public byte GroupCount { get; private set; } // 1 byte, number of DataSet groups
        public uint UNK1 { get; private set; } // 4 bytes, undocumented
        // 3 bytes padding (skipped)
        public uint Configs { get; private set; } // 4 bytes, offset to data chunks
        // 12 bytes padding (skipped)
        // Total: 32 bytes. (4 + 4 + 1 + 4 + 3 + 4 + 12 = 32)

        // DataSet structure (8 bytes per group)
        public class DataSet
        {
            public uint Offset { get; set; } // 4 bytes, pointer to Fileset Data
            public uint Count { get; set; } // 4 bytes, number of Fileset entries
        }
        public List<DataSet> DataSets { get; private set; }

        // Fileset Data structure (32 bytes per entry)
        public class Fileset
        {
            public uint RawOffset { get; set; } // 4 bytes, raw offset with address mode
            public uint Size { get; set; } // 4 bytes, chunk size
            public uint OffsetName { get; set; } // 4 bytes, offset to name data
            public uint ChunkName { get; set; } // 4 bytes, chunk name index
            // 12 bytes padding (skipped)
            public uint UnpackSize { get; set; } // 4 bytes, true size of chunk
            public string[] Names { get; set; } // Parsed names (name, type, directories)
            public uint RealOffset { get; set; } // Processed offset after masking
            public string AddressMode { get; set; } // Type of offset (e.g., Current, RDP)
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
                reader.BaseStream.Seek(12, SeekOrigin.Current); // Skip 12 bytes padding
                fileset.UnpackSize = reader.ReadUInt32();

                // Process offset and address mode
                fileset.AddressMode = GetAddressMode(fileset.RawOffset);
                fileset.RealOffset = ProcessOffset(fileset.RawOffset, fileset.AddressMode);

                // Read names if OffsetName is valid
                if (fileset.OffsetName != 0)
                    fileset.Names = ReadNames(reader, fileset.OffsetName, fileset.ChunkName);

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
            }
        }

        private uint ProcessOffset(uint rawOffset, string addressMode)
        {
            uint offset = rawOffset & 0x00FFFFFF; // Mask first byte
            if (addressMode == "Package" || addressMode == "Data" || addressMode == "Patch")
                offset *= 0x800; // Multiply for RDP files
            return offset;
        }

        private string[] ReadNames(BinaryReader reader, uint offsetName, uint chunkName)
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
                if (sb.Length > 0)
                    names.Add(sb.ToString());
            }

            reader.BaseStream.Seek(originalPosition, SeekOrigin.Begin);
            return names.ToArray();
        }
    }
}