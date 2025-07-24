# Mini Documentation


## RES
#### 1. Class `RES_PSP.cs` (Structure)
- `RES_PSP` handles the structure for RES file.
- It reads the **MAGIC NUMBER/HEADER**, Groups, and the Filesets.


#### 2. Class `RESData.cs` *(Unpacking `-x`)*
- `RESData` is the one handles unpacking. It prints out information and decompresses/extraction files.
- this also handles the serialization (JSON). once unpacking is finished. Useful for creating dictionaries. mostly inspired on gil-unx GEBCS (https://github.com/gil-unx/GEBCS).
- The unpacking procedure skips unnecessary filesets or any RDPs that are not present in the directory. useful for some RES input files with fileset that dont need or require a RDP file.


#### 3. Class `PackRES.cs` *(Repacking `-r`)*
- `PackRES` handles the repacking. this restructures the fileset information, handles proper alignment, and compresses the files required to their respective types. This will resize the original RES file input into a big or small size depending on the edits that user do.

## Others
#### 1. `Inflate.cs`
- This class handles decompression of the **DEFLATE** data.
- Since the `GOD EATER 2` has custom header to identify the compressed **DEFLATE** data. it uses `blz2` as the header information for it. but when inflating/decompressing the data, the header is skipped and only reads the block size (BUFFSIZE) and the compressed data (C_BLOCKS).

#### 2. `Deflate.cs`
- This class handles the compression.
- This also uses the `DEFLATE` class to compress the data.

#### 3. `BLZ4Utils.cs`
- Originally from HaoJun/Randerion: (https://github.com/HaoJun0823/GECV-OLD)
- this class handles the decompression/compression for the later games (e.g GOD EATER RESURRECTION, GOD EATER 2 RAGE BURST, OFFSHOT)