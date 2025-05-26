using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SharpRES
{
    public static class Deflate
    {
        private const uint BLZ2_HEADER = 0x327A6C62; // 'blz2' in little-endian

        
        /* Decompresses a chunk that may contain single or multiple compressed C_BLOCKs.
         For multiple blocks, the first block is moved to the end after decompression.
         Returns raw data if no 'blz2' header is present without prints/logging. */

        public static byte[] DecompressChunk(byte[] chunk, out bool isCompressed)
        {
            isCompressed = false;

            // Minimum size for header (4) + BUFFSIZE (2)
            if (chunk.Length < 6)
                return chunk;

            using (MemoryStream ms = new MemoryStream(chunk))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                // Check for 'blz2' header
                uint header = reader.ReadUInt32();
                if (header != BLZ2_HEADER)
                    return chunk; // No header, return raw data

                isCompressed = true;
                List<byte[]> decompressedBlocks = new List<byte[]>();

                // Read all blocks
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    // Read BUFFSIZE (uint16)
                    if (reader.BaseStream.Length - reader.BaseStream.Position < 2)
                        throw new InvalidDataException("Incomplete BUFFSIZE data in compressed chunk.");

                    ushort buffSize = reader.ReadUInt16();

                    // Validate C_BLOCK size
                    if (reader.BaseStream.Position + buffSize > reader.BaseStream.Length)
                        throw new InvalidDataException("C_BLOCK size exceeds chunk length.");

                    // Read C_BLOCK
                    byte[] cBlock = reader.ReadBytes(buffSize);

                    // Decompress C_BLOCK
                    byte[] decompressedBlock = DecompressCBlock(cBlock);
                    decompressedBlocks.Add(decompressedBlock);
                }

                // Handle block count
                if (decompressedBlocks.Count == 0)
                    throw new InvalidDataException("No valid C_BLOCKs found in compressed chunk.");
                else if (decompressedBlocks.Count == 1)
                {
                    // Single block, no rearrangement needed
                    return decompressedBlocks[0];
                }
                else
                {
                    // Multiple blocks: move first block (tail) to the end
                    List<byte> rearranged = new List<byte>();
                    // Add blocks 1 to N-1 (head and body)
                    for (int i = 1; i < decompressedBlocks.Count; i++)
                        rearranged.AddRange(decompressedBlocks[i]);
                    // Add first block (tail) at the end
                    rearranged.AddRange(decompressedBlocks[0]);
                    return rearranged.ToArray();
                }
            }
        }


        /// Decompresses a single C_BLOCK using raw Deflate (-15 window bits, no ZLIB header).
        
        private static byte[] DecompressCBlock(byte[] cBlock)
        {
            using (MemoryStream input = new MemoryStream(cBlock))
            using (MemoryStream output = new MemoryStream())
            {
                // Use DeflateStream with raw deflate settings
                using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    deflate.CopyTo(output);
                }
                return output.ToArray();
            }
        }
    }
}