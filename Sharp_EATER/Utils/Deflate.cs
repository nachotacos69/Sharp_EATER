// Code is based on BLOCKZIP, just reused here.

/* Order after compression: 
 * Header  
 * Last full chunk 
 * Tail chunk  
 * Full chunks
 */

using System;
using System.IO;
using System.Collections.Generic;
using LibDeflate; // we will now use libdeflate.
public static class Compression
{
    private static readonly byte[] Header = new byte[] { 0x62, 0x6C, 0x7A, 0x32 }; // "blz2" in ASCII
    private const int MaxBlockSize = 0xFFFF; // 64KB max per block

    public static byte[] LeCompression(byte[] inputData)
    {
        Console.WriteLine($"[Debug] Original File Size: {inputData.Length} bytes (0x{inputData.Length:X4})");

        int totalSize = inputData.Length;
        int tailSize = totalSize % MaxBlockSize;
        int fullBlocks = totalSize / MaxBlockSize;

        Console.WriteLine($"[Debug] Tail Size: {tailSize} bytes");
        Console.WriteLine($"[Debug] Full Blocks: {fullBlocks}");

        List<byte[]> compressedBlocks = new List<byte[]>();

        // Compression level 12 for maximum compression
        using (var compressor = new DeflateCompressor(compressionLevel: 12))
        {
            // 1. Compress the Tail (head chunk)
            byte[] tailChunk = new byte[tailSize];
            Array.Copy(inputData, 0, tailChunk, 0, tailSize);
            byte[] compressedTail = DeflateCompress(tailChunk, compressor);
            compressedBlocks.Add(compressedTail);

            // 2. Compress Full 64KB Blocks (body)
            for (int i = 0; i < fullBlocks - 1; i++)
            {
                byte[] block = new byte[MaxBlockSize];
                Array.Copy(inputData, tailSize + (i * MaxBlockSize), block, 0, MaxBlockSize);
                compressedBlocks.Add(DeflateCompress(block, compressor));
            }

            // 3. Compress Last Full 64KB Block (tail)
            if (fullBlocks > 0)
            {
                byte[] lastBlock = new byte[MaxBlockSize];
                Array.Copy(inputData, tailSize + ((fullBlocks - 1) * MaxBlockSize), lastBlock, 0, MaxBlockSize);
                byte[] compressedLastBlock = DeflateCompress(lastBlock, compressor);
                compressedBlocks.Add(compressedLastBlock);
            }
        }

        Console.WriteLine($"[Debug] Total Compressed Blocks: {compressedBlocks.Count}");

        // 4: Rearrange Blocks. Like LEGO, you should "Rearrange your Blocks" after use.
        using (var outputStream = new MemoryStream())
        {
            outputStream.Write(Header, 0, Header.Length);

            // Order: Header → Last full chunk → Tail chunk → Full chunks
            if (compressedBlocks.Count > 1)
                outputStream.Write(compressedBlocks[compressedBlocks.Count - 1], 0, compressedBlocks[compressedBlocks.Count - 1].Length);

            outputStream.Write(compressedBlocks[0], 0, compressedBlocks[0].Length);

            for (int i = 1; i < compressedBlocks.Count - 1; i++)
            {
                outputStream.Write(compressedBlocks[i], 0, compressedBlocks[i].Length);
            }

            Console.WriteLine($"[Debug] Final Compressed File Size: {outputStream.Length} bytes (0x{outputStream.Length:X4})");
            return outputStream.ToArray();
        }
    }

    private static byte[] DeflateCompress(byte[] inputData, DeflateCompressor compressor)
    {
        // DEFLATE (RFC-1951)
        using (var ownedMemory = compressor.Compress(inputData))
        {
            if (ownedMemory == null)
            {
                throw new InvalidOperationException($"Compression failed: output would be larger than input ({inputData.Length} bytes)");
            }

            // Get compressed data from IMemoryOwner
            byte[] compressedData = ownedMemory.Memory.ToArray();

            // Convert length to signed 16-bit (short) and store in Little Endian
            short signedLength = (short)compressedData.Length; // signed short
            byte[] sizeBytes = BitConverter.GetBytes(signedLength);

            if (!BitConverter.IsLittleEndian)
                Array.Reverse(sizeBytes);

            // Creating the final block with signed size prefix
            byte[] finalBlock = new byte[compressedData.Length + 2];
            Array.Copy(sizeBytes, 0, finalBlock, 0, 2);
            Array.Copy(compressedData, 0, finalBlock, 2, compressedData.Length);

            return finalBlock;
        }
    }
}