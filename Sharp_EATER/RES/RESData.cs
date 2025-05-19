using System;

namespace RESExtractor
{
    public class RESData
    {
        private readonly RES_PSP _resFile;
        private readonly bool _PackageRDP;
        private readonly bool _DataRDP;
        private readonly bool _PatchRDP;

        public RESData(RES_PSP resFile, bool PackageRDP, bool DataRDP, bool PatchRDP)
        {
            _resFile = resFile;
            _PackageRDP = PackageRDP;
            _DataRDP = DataRDP;
            _PatchRDP = PatchRDP;
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
                    isValid = _PackageRDP; //package.rdp
                else if(fileset.AddressMode == "Data")
                    isValid = _DataRDP; //data.rdp
                else if (fileset.AddressMode == "Patch")
                    isValid = _PatchRDP; //patch.rdp

                Console.WriteLine($"Fileset {i + 1}:");
                Console.WriteLine($"  Address Mode: {fileset.AddressMode}");
                Console.WriteLine($"  Raw Offset: 0x{fileset.RawOffset:X8}");
                Console.WriteLine($"  Real Offset: 0x{fileset.RealOffset:X8}");
                Console.WriteLine($"  Size: {fileset.Size} bytes");
                Console.WriteLine($"  Unpack Size: {fileset.UnpackSize} bytes");
                Console.WriteLine($"  Offset Name: 0x{fileset.OffsetName:X8}");
                Console.WriteLine($"  Chunk Name Index: {fileset.ChunkName}");

                // Print names
                if (fileset.Names != null && fileset.Names.Length > 0)
                {
                    Console.WriteLine("  Names:");
                    for (int j = 0; j < fileset.Names.Length; j++)
                        Console.WriteLine($"    [{j}]: {fileset.Names[j]}");
                }

                if (!isValid)
                    Console.WriteLine("  [Warning]: Required package/patch file missing.");

                Console.WriteLine();
            }
        }
    }
}