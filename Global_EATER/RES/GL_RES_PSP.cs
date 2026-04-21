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
        // =====================================================================
        // Header structure (32 bytes total)
        // Byte map:
        //   0x00  MagicHeader      (4)
        //   0x04  GroupOffset      (4) - padding (0x00) in the localized format; gonna leave this here
        //   0x08  GroupCount       (1)
        //   0x09  Version          (1)
        //   0x0A  UNK1             (4)
        //   0x0E  [padding]        (2)
        //   0x10  Configs          (4)
        //   0x14  UpdateDataOffset (4)
        //   0x18  UpdateDataSize   (4)
        //   0x1C  Country_Count    (4)
        // =====================================================================

        public uint MagicHeader { get; private set; }

        public uint GroupOffset { get; private set; }
        public byte GroupCount { get; private set; }
        public byte Version { get; private set; }
        public uint UNK1 { get; private set; }
        public uint Configs { get; private set; }
        public uint UpdateDataOffset { get; private set; }
        public uint UpdateDataRealOffset { get; private set; }
        public uint UpdateDataSize { get; private set; }

        public uint Country_Count { get; private set; }

        // Language order tables
        private static readonly string[] LanguageOrder3 = { "EN", "FR", "IT" };
        private static readonly string[] LanguageOrder6 = { "EN", "FR", "IT", "DE", "ES", "RU" };

        public class DataSet
        {
            public uint Offset { get; set; }
            public uint Count { get; set; }
        }
        public class CountrySet
        {
            public string Language { get; set; }
            public uint DataSetOffset { get; set; }
            public uint DataSetLength { get; set; }
        }

        public class LanguageData
        {
            public string Language { get; set; }
            public List<DataSet> DataSets { get; set; }
            public List<Fileset> Filesets { get; set; }
        }

        public class Fileset
        {
            public uint RawOffset { get; set; }
            public uint Size { get; set; }
            public uint OffsetName { get; set; }
            public uint ChunkName { get; set; }
            public uint UnpackSize { get; set; }

            public string[] Names { get; set; }
            public uint RealOffset { get; set; }
            public string AddressMode { get; set; }
            public uint[] NamesPointer { get; set; }
            public bool? CompressedBLZ4 { get; set; }
            public string Filename { get; set; }

            public bool[] GetFilesetPointers() => new[]
            {
                RawOffset  != 0,
                Size       != 0,
                OffsetName != 0,
                ChunkName  != 0,
                UnpackSize != 0
            };
        }

        public List<CountrySet> CountrySets { get; private set; }

        public List<LanguageData> Languages { get; private set; }
        public List<DataSet> DataSets { get; private set; }
        public List<Fileset> Filesets { get; private set; }

        public RES_PSP(BinaryReader reader)
        {
            CountrySets = new List<CountrySet>();
            Languages = new List<LanguageData>();

            MagicHeader = reader.ReadUInt32();
            if (MagicHeader != 0x73657250)
                throw new InvalidDataException("Invalid .res file: Magic header mismatch.");

            GroupOffset = reader.ReadUInt32();
            GroupCount = reader.ReadByte();
            Version = reader.ReadByte();
            UNK1 = reader.ReadUInt32();
            reader.BaseStream.Seek(2, SeekOrigin.Current);
            Configs = reader.ReadUInt32();
            UpdateDataOffset = reader.ReadUInt32();
            UpdateDataSize = reader.ReadUInt32();

            string updateMode = GetAddressMode(UpdateDataOffset);
            UpdateDataRealOffset = ProcessOffset(UpdateDataOffset, updateMode);

            Country_Count = reader.ReadUInt32();


            if (Country_Count > 1)
                ParseLocalized(reader);
            else
                ParseTraditional(reader);
        }


        private void ParseTraditional(BinaryReader reader)
        {
            var langData = new LanguageData
            {
                Language = null,
                DataSets = new List<DataSet>(),
                Filesets = new List<Fileset>()
            };

            long dsStart = (GroupOffset > 0) ? GroupOffset : 0x20L;
            reader.BaseStream.Seek(dsStart, SeekOrigin.Begin);

            for (int i = 0; i < 8; i++)
                langData.DataSets.Add(new DataSet
                {
                    Offset = reader.ReadUInt32(),
                    Count = reader.ReadUInt32()
                });

            reader.BaseStream.Seek(0x60, SeekOrigin.Begin);
            uint total = 0;
            foreach (var ds in langData.DataSets) total += ds.Count;
            for (uint i = 0; i < total; i++)
                langData.Filesets.Add(ReadFileset(reader));

            Languages.Add(langData);
            DataSets = langData.DataSets;
            Filesets = langData.Filesets;
        }


        private void ParseLocalized(BinaryReader reader)
        {
            string[] langOrder = Country_Count == 3 ? LanguageOrder3 : LanguageOrder6;

            for (int i = 0; i < Country_Count; i++)
                CountrySets.Add(new CountrySet
                {
                    Language = langOrder[i],
                    DataSetOffset = reader.ReadUInt32(),
                    DataSetLength = reader.ReadUInt32()
                });

            if (Country_Count == 3)
                reader.BaseStream.Seek(8, SeekOrigin.Current);

            foreach (var cs in CountrySets)
            {
                var langData = new LanguageData
                {
                    Language = cs.Language,
                    DataSets = new List<DataSet>(),
                    Filesets = new List<Fileset>()
                };

                reader.BaseStream.Seek(cs.DataSetOffset, SeekOrigin.Begin);

                for (int i = 0; i < 8; i++)
                    langData.DataSets.Add(new DataSet
                    {
                        Offset = reader.ReadUInt32(),
                        Count = reader.ReadUInt32()
                    });

   
                uint total = 0;
                foreach (var ds in langData.DataSets) total += ds.Count;
                for (uint i = 0; i < total; i++)
                    langData.Filesets.Add(ReadFileset(reader));

                Languages.Add(langData);
            }

            DataSets = Languages.Count > 0 ? Languages[0].DataSets : new List<DataSet>();
            Filesets = Languages.Count > 0 ? Languages[0].Filesets : new List<Fileset>();
        }

        private Fileset ReadFileset(BinaryReader reader)
        {
            var fs = new Fileset
            {
                RawOffset = reader.ReadUInt32(),
                Size = reader.ReadUInt32(),
                OffsetName = reader.ReadUInt32(),
                ChunkName = reader.ReadUInt32()
            };
            reader.BaseStream.Seek(12, SeekOrigin.Current);
            fs.UnpackSize = reader.ReadUInt32();
            fs.AddressMode = GetAddressMode(fs.RawOffset);
            fs.RealOffset = ProcessOffset(fs.RawOffset, fs.AddressMode);

            if (fs.OffsetName != 0)
                (fs.Names, fs.NamesPointer) = ReadNames(reader, fs.OffsetName, fs.ChunkName);

            return fs;
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
            uint offset = rawOffset & 0x00FFFFFF; 
            if (addressMode == "Package" || addressMode == "Data" || addressMode == "Patch")
                offset *= 0x800; 
            return offset;
        }

        private (string[], uint[]) ReadNames(BinaryReader reader, uint offsetName, uint chunkName)
        {
            long savedPos = reader.BaseStream.Position;
            reader.BaseStream.Seek(offsetName, SeekOrigin.Begin);

            uint[] pointers = new uint[chunkName];
            for (uint i = 0; i < chunkName; i++)
                pointers[i] = reader.ReadUInt32();

            var names = new List<string>();
            foreach (uint ptr in pointers)
            {
                reader.BaseStream.Seek(ptr, SeekOrigin.Begin);
                var sb = new StringBuilder();
                char c;
                while ((c = reader.ReadChar()) != '\0')
                    sb.Append(c);
                names.Add(sb.ToString());
            }

            reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
            return (names.ToArray(), pointers);
        }

        public string Serialize()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            object data = (Country_Count <= 1)
                ? SerializeTraditional()
                : SerializeLocalized();

            return JsonSerializer.Serialize(data, options);
        }

        private object SerializeTraditional()
        {
            return new
            {
                MagicHeader,
                GroupOffset, 
                GroupCount,
                Version,
                UNK1,
                Configs,
                UpdateDataOffset,
                UpdateDataRealOffset,
                UpdateDataSize,
                CountryCount = Country_Count,
                DataSets = DataSets.Select(ds => new { ds.Offset, ds.Count }).ToList(),
                Filesets = Filesets.Select(fs => SerializeFileset(fs)).ToList()
            };
        }

        private object SerializeLocalized()
        {
            return new
            {
                MagicHeader,
                GroupCount,
                Version,
                UNK1,
                Configs,
                UpdateDataOffset,
                UpdateDataRealOffset,
                UpdateDataSize,
                CountryCount = Country_Count,
                CountrySets = CountrySets.Select(cs => new
                {
                    cs.Language,
                    cs.DataSetOffset,
                    cs.DataSetLength
                }).ToList(),
                Languages = Languages.Select(ld => new
                {
                    ld.Language,
                    DataSets = ld.DataSets.Select(ds => new { ds.Offset, ds.Count }).ToList(),
                    Filesets = ld.Filesets.Select(fs => SerializeFileset(fs)).ToList()
                }).ToList()
            };
        }

        private object SerializeFileset(Fileset fs)
        {
            return new
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
                fs.CompressedBLZ4,
                fs.Filename
            };
        }
    }
}