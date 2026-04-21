using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SharpRES
{
    public class RESData
    {
        private readonly RES_PSP _resFile;
        private readonly bool _PackageRDP;
        private readonly bool _DataRDP;
        private readonly bool _PatchRDP;
        private readonly string _inputResFile;
        private readonly string _outputFolder;
        private readonly Dictionary<uint, List<string>> _packageDict;
        private readonly Dictionary<uint, List<string>> _dataDict;
        private readonly Dictionary<uint, List<string>> _patchDict;
        private readonly bool _singleFileMode;
        private readonly List<object> _resListTracker;
        private readonly bool _isTopLevelCall;

        private readonly string _langFilter;

        public RESData(RES_PSP resFile, bool PackageRDP, bool DataRDP, bool PatchRDP, string inputResFile,
            string outputFolder = null, Dictionary<uint, List<string>> packageDict = null,
            Dictionary<uint, List<string>> dataDict = null, Dictionary<uint, List<string>> patchDict = null,
            bool singleFileMode = false, List<object> resListTracker = null,
            string langFilter = null)
        {
            _resFile = resFile;
            _PackageRDP = PackageRDP;
            _DataRDP = DataRDP;
            _PatchRDP = PatchRDP;
            _inputResFile = inputResFile;
            _outputFolder = outputFolder ?? Path.GetFileNameWithoutExtension(inputResFile);
            _packageDict = packageDict;
            _dataDict = dataDict;
            _patchDict = patchDict;
            _singleFileMode = singleFileMode;
            _langFilter = langFilter;


            _isTopLevelCall = (packageDict == null && dataDict == null && patchDict == null);


            _resListTracker = resListTracker ?? (_isTopLevelCall ? new List<object>() : null);
        }

        public void PrintInformation()
        {
            Console.WriteLine("=== Header ===");
            Console.WriteLine($"Magic Header: 0x{_resFile.MagicHeader:X8}");

            if (_resFile.Country_Count <= 1)
                Console.WriteLine($"Group Offset: 0x{_resFile.GroupOffset:X8}");

            Console.WriteLine($"Group Count: {_resFile.GroupCount}");
            Console.WriteLine($"RES Version: {_resFile.Version}");
            Console.WriteLine($"Configs Offset: 0x{_resFile.Configs:X8}");
            Console.WriteLine($"Update Data Offset + Update Data RealOffset: 0x{_resFile.UpdateDataOffset:X8} (0x{_resFile.UpdateDataRealOffset:X8})");
            Console.WriteLine($"Update Data Size: 0x{_resFile.UpdateDataSize:X8}");
            Console.WriteLine($"Country Count: {_resFile.Country_Count}");
            Console.WriteLine();

            if (_resFile.Country_Count > 1)
            {

                Console.WriteLine("=== CountrySets ===");
                foreach (var cs in _resFile.CountrySets)
                    Console.WriteLine($"  [{cs.Language}] DataSetOffset=0x{cs.DataSetOffset:X8}, DataSetLength=0x{cs.DataSetLength:X8}");
                Console.WriteLine();

                foreach (var langData in _resFile.Languages)
                {
                    Console.WriteLine($"=== [{langData.Language}] DataSets ===");
                    for (int i = 0; i < langData.DataSets.Count; i++)
                        Console.WriteLine($"DataSet {i + 1}: Offset=0x{langData.DataSets[i].Offset:X8}, Count={langData.DataSets[i].Count}");
                    Console.WriteLine();

                    Console.WriteLine($"=== [{langData.Language}] Filesets ===");
                    PrintFilesetsInfo(langData.Filesets);
                    Console.WriteLine();
                }
            }
            else
            {
                Console.WriteLine("=== DataSets ===");
                for (int i = 0; i < _resFile.DataSets.Count; i++)
                    Console.WriteLine($"DataSet {i + 1}: Offset=0x{_resFile.DataSets[i].Offset:X8}, Count={_resFile.DataSets[i].Count}");
                Console.WriteLine();

                Console.WriteLine("=== Filesets ===");
                PrintFilesetsInfo(_resFile.Filesets);
            }

            ExtractFiles();
        }

        private void PrintFilesetsInfo(IList<RES_PSP.Fileset> filesets)
        {
            for (int i = 0; i < filesets.Count; i++)
            {
                var fileset = filesets[i];

                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Reserve/Dummy]");
                    continue;
                }

                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName != 0 && fileset.ChunkName != 0 && fileset.UnpackSize == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Reserve/Empty]");
                    continue;
                }

                bool isValid = true;
                if (fileset.AddressMode == "Package") isValid = _PackageRDP;
                else if (fileset.AddressMode == "Data") isValid = _DataRDP;
                else if (fileset.AddressMode == "Patch") isValid = _PatchRDP;

                Console.WriteLine($"Fileset {i + 1}:");
                Console.WriteLine($"  Address Mode: {fileset.AddressMode}");
                Console.WriteLine($"  Raw Offset: 0x{fileset.RawOffset:X8}");
                Console.WriteLine($"  Real Offset: 0x{fileset.RealOffset:X8}");
                Console.WriteLine($"  Size: {fileset.Size} bytes");
                Console.WriteLine($"  Unpack Size: {fileset.UnpackSize} bytes");
                Console.WriteLine($"  Offset Name: 0x{fileset.OffsetName:X8}");
                Console.WriteLine($"  Chunk Name Index: {fileset.ChunkName}");

                if (fileset.Names != null && fileset.Names.Length > 0)
                {
                    Console.WriteLine("  Names:");
                    for (int j = 0; j < fileset.Names.Length; j++)
                        Console.WriteLine($"    [{j}]: {fileset.Names[j]}");
                }

                if (!isValid)
                    Console.WriteLine("  [Warning]: package/data/patch files missing.");

                Console.WriteLine();
            }
        }

        private void ExtractFiles()
        {

            var packageDict = _packageDict ?? new Dictionary<uint, List<string>>();
            var dataDict = _dataDict ?? new Dictionary<uint, List<string>>();
            var patchDict = _patchDict ?? new Dictionary<uint, List<string>>();

            var allResFiles = new List<string>();
            var allRtblFiles = new List<string>();

            if (_resFile.Country_Count > 1)
            {

                if (_langFilter != null)
                {
                    bool langExists = _resFile.Languages.Any(
                        ld => string.Equals(ld.Language, _langFilter, StringComparison.OrdinalIgnoreCase));

                    if (!langExists)
                    {
                        // Build a friendly list of what IS available.
                        string available = string.Join(", ", _resFile.Languages.Select(ld => ld.Language));
                        Console.WriteLine($"[Error] Language '{_langFilter}' is not available in this RES file.");
                        Console.WriteLine($"  This file has {_resFile.Country_Count} language(s): {available}");
                        Console.WriteLine("  Extraction aborted. Please re-run with one of the available language codes.");
                        return;
                    }
                }

                var langsToProcess = (_langFilter != null)
                    ? _resFile.Languages.Where(ld => string.Equals(ld.Language, _langFilter, StringComparison.OrdinalIgnoreCase)).ToList()
                    : _resFile.Languages.ToList();

                if (_langFilter != null)
                    Console.WriteLine($"[Info] Language filter active: extracting '{_langFilter}' only.");

                foreach (var langData in langsToProcess)
                {
                    // direct extract with no folder sorting if a `-LANG` args is applied

                    string langFolder = (_langFilter != null)
                        ? _outputFolder
                        : Path.Combine(_outputFolder, langData.Language);
                    Console.WriteLine($"\n=== Extracting Language: {langData.Language} ===");

                    var (resFiles, rtblFiles) = ExtractFilesForLanguage(
                        langData.Filesets, langFolder, packageDict, dataDict, patchDict);

                    allResFiles.AddRange(resFiles);
                    allRtblFiles.AddRange(rtblFiles);
                }
            }
            else
            {

                if (_langFilter != null)
                {
                    Console.WriteLine($"[Warning] '-LANG {_langFilter}' was specified, but '{Path.GetFileName(_inputResFile)}' is not a localized RES file (Country_Count={_resFile.Country_Count}).");
                    Console.WriteLine("  The language filter will be ignored and all content will be extracted.");
                }

                var (resFiles, rtblFiles) = ExtractFilesForLanguage(
                    _resFile.Filesets, _outputFolder, packageDict, dataDict, patchDict);

                allResFiles.AddRange(resFiles);
                allRtblFiles.AddRange(rtblFiles);
            }

            if (!_singleFileMode)
            {
                ExtractNestedRtblFiles(allRtblFiles, packageDict, dataDict, patchDict);
                ExtractNestedResFiles(allResFiles, packageDict, dataDict, patchDict);
            }
            else if (allResFiles.Count > 0 || allRtblFiles.Count > 0)
            {
                Console.WriteLine($"\nSkipping extraction of {allResFiles.Count} nested RES and {allRtblFiles.Count} nested RTBL file(s) due to -single flag.");
            }

            if (_isTopLevelCall)
            {
                SerializeRDPDictionaries(packageDict, "packageDict.json");
                SerializeRDPDictionaries(dataDict, "dataDict.json");
                SerializeRDPDictionaries(patchDict, "patchDict.json");
                SerializeResList();
            }
        }

        private (List<string> resFiles, List<string> rtblFiles) ExtractFilesForLanguage(
            IList<RES_PSP.Fileset> filesets,
            string langOutputFolder,
            Dictionary<uint, List<string>> packageDict,
            Dictionary<uint, List<string>> dataDict,
            Dictionary<uint, List<string>> patchDict)
        {
            Console.WriteLine("=== Extracting Files ===");
            var existingFiles = new HashSet<string>();
            var resFiles = new List<string>();
            var rtblFiles = new List<string>();

            for (int i = 0; i < filesets.Count; i++)
            {
                var fileset = filesets[i];

                if (fileset.RawOffset == 0 && fileset.Size == 0 && fileset.OffsetName == 0 && fileset.ChunkName == 0)
                {
                    Console.WriteLine($"Fileset {i + 1}: [Dummy] - Skipped extraction.");
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                    continue;
                }

                string sourceFile = null;
                Dictionary<uint, List<string>> targetDict = null;

                switch (fileset.AddressMode)
                {
                    case "Package":
                        if (!_PackageRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Package] - Missing package.rdp, skipped.");
                            fileset.CompressedBLZ4 = null; fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "package.rdp"; targetDict = packageDict; break;

                    case "Data":
                        if (!_DataRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Data] - Missing data.rdp, skipped.");
                            fileset.CompressedBLZ4 = null; fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "data.rdp"; targetDict = dataDict; break;

                    case "Patch":
                        if (!_PatchRDP)
                        {
                            Console.WriteLine($"Fileset {i + 1}: [Patch] - Missing patch.rdp, skipped.");
                            fileset.CompressedBLZ4 = null; fileset.Filename = null;
                            continue;
                        }
                        sourceFile = "patch.rdp"; targetDict = patchDict; break;

                    case "SET_C":
                    case "SET_D":
                        sourceFile = _inputResFile; break;

                    case "Reserve":
                    case "Empty":

                        break;

                    default:
                        Console.WriteLine($"Fileset {i + 1}: [Invalid Address Mode] - Skipped extraction.");
                        fileset.CompressedBLZ4 = null; fileset.Filename = null;
                        continue;
                }

                string outputPath = ConstructOutputPath(fileset, existingFiles, i + 1, langOutputFolder);
                if (string.IsNullOrEmpty(outputPath))
                {
                    Console.WriteLine($"Fileset {i + 1}: [Invalid Names] - Skipped extraction.");
                    fileset.CompressedBLZ4 = null; fileset.Filename = null;
                    continue;
                }

                try
                {
                    string directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    if (fileset.AddressMode == "Reserve" || fileset.AddressMode == "Empty" || fileset.Size == 0)
                    {
                        File.WriteAllBytes(outputPath, Array.Empty<byte>());
                        Console.WriteLine($"Fileset {i + 1}: Created empty file at {outputPath}");
                        fileset.CompressedBLZ4 = false;
                        fileset.Filename = outputPath;
                    }
                    else
                    {

                        uint actualReadSize = fileset.Size;
                        bool isStackedData = false;

                        if (fileset.ChunkName == 1
                            && (fileset.AddressMode == "SET_C" || fileset.AddressMode == "SET_D")
                            && fileset.OffsetName > fileset.RealOffset)
                        {
                            actualReadSize = fileset.OffsetName - fileset.RealOffset;
                            fileset.UnpackSize = actualReadSize;
                            isStackedData = true;
                            Console.WriteLine($"Fileset {i + 1}: Detected stacked data (ChunkName=1). Calculated actual size: {actualReadSize} bytes (Original size: {fileset.Size} bytes)");
                        }

                        byte[] chunk;
                        using (var reader = new BinaryReader(File.Open(sourceFile, FileMode.Open)))
                        {
                            reader.BaseStream.Seek(fileset.RealOffset, SeekOrigin.Begin);
                            chunk = reader.ReadBytes((int)actualReadSize);
                        }

                        byte[] outputData;
                        bool isBLZ4 = false;

                        if (BLZ4Utils.IsBLZ4(chunk))
                        {
                            outputData = BLZ4Utils.UnpackBLZ4Data(chunk);
                            isBLZ4 = true;
                        }
                        else
                        {

                            outputData = chunk;
                        }

                        File.WriteAllBytes(outputPath, outputData);
                        fileset.CompressedBLZ4 = isBLZ4;
                        fileset.Filename = outputPath;

                        if (targetDict != null)
                        {
                            if (!targetDict.ContainsKey(fileset.RealOffset))
                                targetDict[fileset.RealOffset] = new List<string>();
                            targetDict[fileset.RealOffset].Add(outputPath);
                        }

                        if (isBLZ4)
                            Console.WriteLine($"Fileset {i + 1}: Decompressed BLZ4 {chunk.Length} bytes to {outputData.Length} bytes at {outputPath}");
                        else if (isStackedData)
                            Console.WriteLine($"Fileset {i + 1}: Extracted {chunk.Length} bytes (stacked data) at {outputPath}");
                        else
                            Console.WriteLine($"Fileset {i + 1}: Extracted {chunk.Length} bytes (raw) at {outputPath}");
                    }

                    string ext = Path.GetExtension(outputPath).ToLower();
                    if (ext == ".res") resFiles.Add(outputPath);
                    else if (ext == ".rtbl") rtblFiles.Add(outputPath);

                    existingFiles.Add(outputPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fileset {i + 1}: Failed to extract to {outputPath}. Error: {ex.Message}");
                    fileset.CompressedBLZ4 = null;
                    fileset.Filename = null;
                }
            }

            return (resFiles, rtblFiles);
        }


        private void ExtractNestedRtblFiles(List<string> rtblFiles,
            Dictionary<uint, List<string>> packageDict,
            Dictionary<uint, List<string>> dataDict,
            Dictionary<uint, List<string>> patchDict)
        {
            if (rtblFiles.Count == 0) return;

            Console.WriteLine($"\n=== Processing {rtblFiles.Count} Nested RTBL Files ===");

            foreach (var rtblFilePath in rtblFiles)
            {
                Console.WriteLine($"\n--- Processing nested RTBL file: {rtblFilePath} ---");
                try
                {
                    RTBL rtblFile = new RTBL(rtblFilePath);
                    rtblFile.Unpack(packageDict, dataDict, patchDict, _resListTracker);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process nested RTBL file {rtblFilePath}: {ex.Message}");
                }
            }
        }

        private void ExtractNestedResFiles(List<string> resFiles,
            Dictionary<uint, List<string>> packageDict,
            Dictionary<uint, List<string>> dataDict,
            Dictionary<uint, List<string>> patchDict)
        {
            if (resFiles.Count == 0) return;

            Console.WriteLine($"\n=== Extracting {resFiles.Count} Nested RES Files ===");

            for (int i = 0; i < resFiles.Count; i++)
            {
                string resFilePath = resFiles[i];
                Console.WriteLine($"\n--- Processing nested RES file {i + 1}: {resFilePath} ---");

                try
                {
                    RES_PSP resFile;
                    using (var reader = new BinaryReader(File.Open(resFilePath, FileMode.Open)))
                        resFile = new RES_PSP(reader);

                    string resOutputFolder = Path.Combine(
                        Path.GetDirectoryName(resFilePath),
                        Path.GetFileNameWithoutExtension(resFilePath));


                    var resData = new RESData(resFile, _PackageRDP, _DataRDP, _PatchRDP,
                        resFilePath, resOutputFolder, packageDict, dataDict, patchDict,
                        false, _resListTracker, _langFilter);
                    resData.PrintInformation();

                    string outputJsonFile = Path.ChangeExtension(resFilePath, ".json");
                    File.WriteAllText(outputJsonFile, resFile.Serialize());
                    Console.WriteLine($"Nested RES serialization complete. Output saved to {outputJsonFile}");

                    _resListTracker?.Add(new
                    {
                        Index = _resListTracker.Count + 1,
                        ResFilePath = resFilePath,
                        JsonFilePath = outputJsonFile
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process nested RES file {resFilePath}: {ex.Message}");
                }
            }
        }

        private void SerializeResList()
        {
            if (_resListTracker == null || _resListTracker.Count == 0)
                return;

            var outputDir = Path.GetDirectoryName(_inputResFile);
            var resListPath = string.IsNullOrEmpty(outputDir)
                ? "RESList.json"
                : Path.Combine(outputDir, "RESList.json");

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            if (!string.IsNullOrEmpty(Path.GetDirectoryName(resListPath)))
                Directory.CreateDirectory(Path.GetDirectoryName(resListPath));

            File.WriteAllText(resListPath, JsonSerializer.Serialize(_resListTracker, options));
            Console.WriteLine($"\nRESList saved to {resListPath}");
        }

        private void SerializeRDPDictionaries(Dictionary<uint, List<string>> dict, string outputFileName)
        {
            if (dict.Count == 0)
            {
                Console.WriteLine($"Skipping {outputFileName}: No entries to serialize.");
                return;
            }

            var outputDir = Path.GetDirectoryName(_inputResFile);
            var outputPath = string.IsNullOrEmpty(outputDir)
                ? outputFileName
                : Path.Combine(outputDir, outputFileName);

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

            if (!string.IsNullOrEmpty(Path.GetDirectoryName(outputPath)))
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            File.WriteAllText(outputPath, JsonSerializer.Serialize(serializedData, options));
            Console.WriteLine($"RDP dictionary saved to {outputPath}");
        }


        private string ConstructOutputPath(
            RES_PSP.Fileset fileset,
            HashSet<string> existingFiles,
            int filesetIndex,
            string baseFolder)
        {
            if (fileset.Names == null || fileset.Names.Length == 0)
                return null;

            string fileName = SanitizeChar(fileset.Names[0]);
            string extension = fileset.Names.Length > 1 ? SanitizeChar(fileset.Names[1]) : "";
            var directories = fileset.Names.Length > 2
                ? fileset.Names.Skip(2).Select(SanitizeChar).ToList()
                : new List<string>();

            directories.Insert(0, baseFolder);

            string directoryPath = string.Join(Path.DirectorySeparatorChar.ToString(), directories);
            string baseFileName = string.IsNullOrEmpty(extension) ? fileName : $"{fileName}.{extension}";
            string outputPath = string.IsNullOrEmpty(directoryPath)
                ? Path.Combine(baseFolder, baseFileName)
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
                    ? Path.Combine(baseFolder, newFileName)
                    : Path.Combine(directoryPath, newFileName);
                counter++;
            }

            return finalPath;
        }

        private static string SanitizeChar(string segment)
        {
            if (string.IsNullOrEmpty(segment))
                return segment;

            segment = segment.Replace('/', '\\');

            char[] invalidChars = Path.GetInvalidFileNameChars()
                .Union(Path.GetInvalidPathChars())
                .Except(new[] { Path.DirectorySeparatorChar })
                .ToArray();

            return string.Concat(segment.Select(c => invalidChars.Contains(c) ? '_' : c));
        }
    }
}