# Usage
This is purely a simple example of how the implementation can be acheived. Please see [UlyssesWu/LZ4.Frame](https://github.com/UlyssesWu/LZ4.Frame) for a usable implementation

# Intro
Example C# implementation of reading and writing files to/from LZ4 format
See the specification for details: https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md

The LZ4 frame format does not support multiple files in one LZ4 frame.
To work with directories, either put everything in a tarball and compress it
Or compress all the files an put them in a tarball.

SharpCompress and SharpZipLib supports tar

# Packages
This software relies on two packages

- lz4net
- xxHash

# Compatibility
I've been testing the code against the LZ4 binary available on Ubuntu 16.04 by Yann Collet.

Files compressed with this code are compatible and can be decompressed by the LZ4 binary.

Files compressed by the LZ4 binary with the `--content-size` flag decompress with this code.

I'm currently have trouble with compressing ~~and decompressing~~ files over 4MB. It works fine internally, but it's no longer functional between this code and the LZ4 binary, so I probably implemented something wrong with how multiple blocks are handled (since max size of each block is 4MB).
